#include <stdio.h>
#include <stdlib.h>
long g_driver_result = -1;
void record_sum(long v) { g_driver_result = v; }
extern void gata_App_T_main(void);
int main(void) {
    gata_App_T_main();
    printf("checksum=%ld\n", g_driver_result);
    if (g_driver_result != 50294) { fprintf(stderr, "MISMATCH expected 50294\n"); return 2; }
    printf("ASAN_REGRESSION_OK\n");
    return 0;
}
