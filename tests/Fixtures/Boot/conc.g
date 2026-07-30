/*
 * conc.g - Concurrency coverage for the boot image: several processes, several threads each,
 * all contending for one shared object through libgata's Sync primitives.
 *
 * Nothing else in the suite runs two Gata threads against the same state. Threads take no
 * parameters and Gata has no globals, so the only way two of them reach one object is a
 * native slot. The pointer is published with release/acquire ordering, so a reader that sees
 * it also sees a fully constructed object, and the setter takes ownership by pinning: the
 * slot is a raw pointer ARC cannot see, and without the pin the object dies when the thread
 * that made it ends. The getter hands back a retained reference, because a native body
 * returning a managed value at +0 would net-decrement the count on every call - the caller's
 * scope releases what it was given.
 *
 * A thread cannot print (a kernel thread's Console goes to its process TTY, not the serial
 * console, and 'debug' takes only a literal), so each reporter checks the invariants itself
 * and announces a verdict marker. BootTests asserts the '-ok' markers appear and that no
 * '-BAD' marker appears anywhere.
 */

import Console;
import Sync;
import Sys;

native {
    static void* _kshared = 0;
    static void* _ushared = 0;
}

class Shared {
    public AtomicInt hits;      // one atomic add per unit of work
    public AtomicInt tickets;   // handed out by compare-exchange, so each value goes to one thread
    public AtomicInt ticketSum; // the sum of every claimed ticket: pins uniqueness, not just the count
    public AtomicInt countdown; // Decrement, from the expected total back to zero
    public AtomicInt done;      // workers that have finished
    public SpinLock  guard;
    public int64     plain;     // NOT atomic: correct only if the lock actually excludes

    func _init() {
        self.hits = new AtomicInt();
        self.tickets = new AtomicInt();
        self.ticketSum = new AtomicInt();
        self.countdown = new AtomicInt();
        self.done = new AtomicInt();
        self.guard = new SpinLock();
        self.plain = 0 as int64;
    }
}

module KShared {
    public void func Set(Shared s) native {
        __atomic_add_fetch(&((gata_obj*)s)->__rc, 1, __ATOMIC_RELAXED);
        __atomic_store_n(&_kshared, (void*)s, __ATOMIC_RELEASE);
    }
    public Shared func Get() native {
        void* p = __atomic_load_n(&_kshared, __ATOMIC_ACQUIRE);
        if (p) __atomic_add_fetch(&((gata_obj*)p)->__rc, 1, __ATOMIC_RELAXED);
        return p;
    }
    public int64 func Rc() native {
        void* p = __atomic_load_n(&_kshared, __ATOMIC_ACQUIRE);
        return p ? (int64_t)__atomic_load_n(&((gata_obj*)p)->__rc, __ATOMIC_RELAXED) : (int64_t)-1;
    }
}

module UShared {
    public void func Set(Shared s) native {
        __atomic_add_fetch(&((gata_obj*)s)->__rc, 1, __ATOMIC_RELAXED);
        __atomic_store_n(&_ushared, (void*)s, __ATOMIC_RELEASE);
    }
    public Shared func Get() native {
        void* p = __atomic_load_n(&_ushared, __ATOMIC_ACQUIRE);
        if (p) __atomic_add_fetch(&((gata_obj*)p)->__rc, 1, __ATOMIC_RELAXED);
        return p;
    }
    public int64 func Rc() native {
        void* p = __atomic_load_n(&_ushared, __ATOMIC_ACQUIRE);
        return p ? (int64_t)__atomic_load_n(&((gata_obj*)p)->__rc, __ATOMIC_RELAXED) : (int64_t)-1;
    }
}

int func ConcKWorkers() { return 4; }
int func ConcUWorkers() { return 2; }

/*
 * ConcIters - Work per thread, lower in userspace.
 *
 * A userspace iteration costs more than a kernel one - every yield is a syscall and the
 * threads run in their own address space - and the whole image has a fixed QEMU budget it
 * shares with the userspace thread main.g already had. At equal counts one or the other
 * intermittently missed it on a loaded machine, which is a flaky test rather than a finding.
 * The kernel side carries the volume; the userspace side is here to prove the same
 * primitives work in a separate address space.
 */
int func ConcIters(bool kernelSide) {
    if (kernelSide) { return 200; }
    return 30;
}

/*
 * ClaimTicket - Takes the next ticket with compare-exchange, retrying until it wins.
 *
 * Increment would count correctly too; what this pins is that no two threads ever observe
 * the same value, which only a compare-exchange can establish.
 */
int64 func ClaimTicket(AtomicInt t) {
    while (true) {
        let int64 cur = t.Get();
        if (t.CompareExchange(cur, cur + (1 as int64))) { return cur; }
    }
}

// Spins until the shared object has been published, yielding so the publisher can run.
Shared func AwaitShared(bool kernelSide) {
    let Shared s = null;
    if (kernelSide) { s = KShared.Get(); } else { s = UShared.Get(); }
    while (s == null) {
        Sys.Yield();
        if (kernelSide) { s = KShared.Get(); } else { s = UShared.Get(); }
    }
    return s;
}

/*
 * ChurnRef - Takes and drops a reference to the shared object.
 *
 * The compiler emits a retain here and a release at the end of the scope, so with every
 * worker calling it the refcount itself is under contention, not only the fields.
 */
void func ChurnRef(bool kernelSide) {
    let Shared s = AwaitShared(kernelSide);
    if (s == null) { return; }
}

void func Grind(Shared s, bool kernelSide) {
    let int i = 0;
    while (i < ConcIters(kernelSide)) {
        s.hits.Increment();

        // The lock's whole job: 'plain' is an ordinary field, so a read-modify-write that is
        // not excluded loses updates and the total comes out short.
        s.guard.Lock();
        s.plain = s.plain + (1 as int64);
        s.guard.Unlock();

        s.ticketSum.Add(ClaimTicket(s.tickets));
        s.countdown.Decrement();
        ChurnRef(kernelSide);
        i = i + 1;
    }
    s.done.Increment();
}

Shared func PrepareShared(int workers, bool kernelSide) {
    let Shared s = new Shared();
    s.countdown.Set((workers * ConcIters(kernelSide)) as int64);
    return s;
}

/*
 * VerifyShared - Every invariant at once, since a thread can only report a verdict.
 *
 * The sum of tickets is the sharp one: with n tickets handed out exactly once it must be
 * 0+1+...+(n-1), so a duplicate or a lost claim moves it even when the count is right.
 */
bool func VerifyShared(Shared s, int workers, bool kernelSide) {
    let int64 total = (workers * ConcIters(kernelSide)) as int64;
    let int64 wantSum = total * (total - (1 as int64)) / (2 as int64);
    if (s.hits.Get() != total) { return false; }
    if (s.plain != total) { return false; }
    if (s.tickets.Get() != total) { return false; }
    if (s.ticketSum.Get() != wantSum) { return false; }
    if (s.countdown.Get() != (0 as int64)) { return false; }
    return true;
}

/*
 * SyncBasics - Uncontended semantics, where the expected answer is unambiguous.
 *
 * Worth pinning separately: under contention a wrong TryLock or compare-exchange can still
 * produce the right total, so the concurrent checks below would not catch it.
 */
bool func SyncBasics() {
    let AtomicInt a = new AtomicInt();
    a.Set(10 as int64);
    a.Add(5 as int64);          // 15
    a.Increment();              // 16
    a.Decrement();              // 15
    let bool casTaken = a.CompareExchange(15 as int64, 100 as int64);
    let bool casRefused = a.CompareExchange(15 as int64, 999 as int64);

    let SpinLock l = new SpinLock();
    let bool freeAtFirst = l.TryLock();
    let bool refusedWhileHeld = l.TryLock();
    l.Unlock();
    let bool freeAgain = l.TryLock();
    l.Unlock();

    return a.Get() == (100 as int64) && casTaken && !casRefused
           && freeAtFirst && !refusedWhileHeld && freeAgain;
}
