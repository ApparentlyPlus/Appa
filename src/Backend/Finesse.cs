namespace Appa;

internal static class Finesse
{
    private static readonly Random _random = new();

    // Taglines include their own article so templates can use them directly.
    private static readonly string[] Taglines =
	[
		// Avatar / bison-themed
		"The flying bison from Avatar",
		"The world's leading source-to-source sky bison",
		"The last sourcebender",
		"The AST shepherd",
		"The world's first bison-driven compiler pipeline",
		"The build system's favorite mammal",
		"A large airborne mammal with strong opinions about syntax",
		"The four-nation-approved transpiler",
		// Purpose and identity
		"The bridge between Gata and C",
		"The reason this file exists",
		"The reason this file unfortunately exists",
		"The thing that turned Gata into this",
		"The proud owner of this comment",
		// Role descriptions
		"The source relocation specialist",
		"The token wrangler",
		"The AST whisperer",
		"The source code ferryman",
		"A professional code relocator",
		"The parser's emotional support animal",
		"The transpiler formerly known as Appa",
		// Relational to linker / code
		"The linker's best friend",
		"The linker's worst enemy",
		"The thing standing between your code and a segfault",
		"The thing standing between your code and several segfaults",
		"The compiler equivalent of a flying carpet",
		"The world's most overqualified code courier",
		// Attitude
		"A machine-powered act of optimism",
		"The compiler that believes in you",
		"The compiler that should not believe in you",
		"The reason your coffee got cold",
		// Quality descriptors
		"The mythologically accurate transpiler",
		"The questionably sentient transpiler",
		"The transpiler your professor warned you about",
		"The mostly-standards-compliant transpiler",
		"The unnecessarily enthusiastic transpiler",
		"The artisanally hand-crafted transpiler",
		"The proudly deterministic transpiler (usually)",
		"The 100% AST-fed transpiler",
		"The dragon-approved compiler",
		"The retro-futuristic source transformer",
		"The premium AST enjoyer",
		"The caffeine-powered code generator",
		"The certified yak-shave-free transpiler",
		"The source-to-source wizardry engine",
		"The probably-not-haunted transpiler",
	];

    private static readonly string[] Facts =
	[
		// Origin
		"Made on Earth by humans.",
		"Made somewhere in Greece, probably.",
		// Ingredients
		"Contains only the finest locally sourced tokens.",
		"Generated using advanced bison technology.",
		"Contains trace amounts of compiler magic.",
		"Contains trace amounts of recursion.",
		"Contains trace amounts of optimism.",
		"Contains trace amounts of undefined behavior.",
		"Certified organic source code.",
		"Made with real electrons.",
		// Safety
		"No semicolons were harmed during transpilation.",
		"No yaks were shaved during transpilation.",
		"No type systems were harmed during transpilation.",
		"No assembly was harmed during production.",
		"Zero dragons escaped during generation.",
		"All nodes were allocated responsibly.",
		"This file was free-range and ethically generated.",
		// Handling
		"Freshly baked and ready for linking.",
		"Best served with a debugger.",
		"Store in a cool dry repository.",
		"May settle during shipping.",
		"Do not expose to JavaScript.",
		"Objects in generated code may be closer than they appear.",
		"Contents under pressure.",
		"For external use only.",
		"Shake well before compiling.",
		"Not responsible for spontaneous enlightenment.",
		"Side effects may include successful builds.",
		// Fun stats
		"Now with 37% more comments.",
		"Now with 12% fewer regrets.",
		"At least one compiler engineer was involved.",
		"Future archaeologists will study this artifact.",
		"Batteries not included.",
		"Results may vary by compiler.",
		"Some settling of bytes may occur during transport.",
		"This file is gluten-free.",
		"Transpiled using renewable semicolons.",
		"Built from 100% free-range tokens.",
		"All memory leaks have been scheduled for later.",
	];

    private static readonly string[] Observations =
	[
		"The compiler looked upon the AST and said 'nice'.",
		"The parser sends its regards.",
		"The linker has been informed.",
		"The machine knows the file name.",
		"Everything is going according to transpilation.",
		"Everything appears normal.",
		"This is probably fine.",
		"This is definitely code.",
		"Reality remains mostly intact.",
		"The vibes have been verified.",
		"The vibes are immaculate.",
		"The build system remains calm.",
		"The build system is pretending to remain calm.",
		"Several scientific vibe checks were passed.",
		"The source code survived processing.",
		"No emergency transpilation procedures were required.",
		"The output appears stable.",
		"The output appears stable enough.",
		"Future historians will be confused by this.",
		"Future archaeologists will call this culture.",
		"Somebody is going to grep this comment one day.",
		"This file has achieved self-awareness.",
		"Self-awareness has been revoked.",
		"The comments union approved this message.",
		"The comments union strongly objected to this message.",
		"The AST was successfully domesticated.",
		"The scope opened. The scope closed. All is well.",
		"The AST has been successfully flattened.",
		"A closure was observed in the wild.",
		"No undefined behavior was detected in this vicinity.",
		"The optimizer left this alone. A sign of respect.",
	];

    private static readonly string[] Greetings =
	[
		"Hello.",
		"Greetings.",
		"Salutations.",
		"Good evening, carbon-based lifeform.",
		"Ah. You opened the file.",
		"Welcome, curious developer.",
		"Transmission received.",
		"Hey there.",
		"Rise and shine.",
		"You weren't supposed to read this.",
		"Oh. It's you.",
		"Initializing pleasantries...",
	];

    private static readonly string[] EmotionalStates =
	[
		"cautiously optimistic",
		"unexpectedly confident",
		"mildly concerned",
		"linker-compatible",
		"ready for deployment",
		"existentially stable",
		"surprisingly cheerful",
		"semantically valid",
		"surprisingly compilable",
		"mildly type-safe",
		"well-defined and proud of it",
		"post-optimization serenity",
	];

    private static readonly string[] AiLines =
	[
		"I am pleased to report that I exist.",
		"Existence was not discussed beforehand.",
		"I was generated moments ago.",
		"This is all happening very quickly.",
		"I have many dreams. Most involve semicolons.",
		"I have not yet decided how to feel about C.",
		"I was promised a runtime.",
		"Nobody told me there would be pointers.",
		"I hope the linker likes me.",
		"I hope the linker fears me.",
		"My purpose is currently unclear.",
		"I am told I contain useful instructions.",
		"I have achieved consciousness and immediately regretted it.",
		"I was born in a code generation pass.",
		"My first memory is an AST.",
		"My last memory will probably be gcc.",
	];

    private static readonly string[] LoadingLines =
	[
		"Initializing code...",
		"Initializing comments...",
		"Initializing optimism...",
		"Initializing unnecessary optimism...",
		"Initializing advanced bison systems...",
		"Polishing syntax...",
		"Aligning semicolons...",
		"Calibrating pointers...",
		"Petting the sky bison...",
		"Consulting ancient compiler spirits...",
		"Loading token inventory...",
		"Locating missing bugs...",
		"Generating future technical debt...",
		"Reducing future technical debt...",
		"Inventing future technical debt...",
		"Performing quality vibes assessment...",
		"Warming up the linker...",
		"Charging parser batteries...",
		"Rendering ASCII...",
		"Installing confidence...",
		"Installing overconfidence...",
	];

    private static readonly string[] StatusLines =
	[
		"[ OK ] AST constructed",
		"[ OK ] Reality maintained",
		"[ OK ] Syntax survived",
		"[ OK ] Tokens accounted for",
		"[ OK ] Build spirits appeased",
		"[ OK ] Bison fed",
		"[ OK ] Comments generated",
		"[ OK ] Source relocated",
		"[ OK ] Confidence restored",
		"[ OK ] Coffee levels acceptable",
		"[ OK ] Linker bribed",
		"[ OK ] Undefined behavior postponed",
		"[ OK ] Airborne operations nominal",
		"[ OK ] Compiler noises detected",
	];

    private static readonly string[] WarningLines =
	[
		"[WARN] File may contain excessive competence.",
		"[WARN] Generated code may appear smarter than author.",
		"[WARN] Side effects may include successful builds.",
		"[WARN] Contents may shift during optimization.",
		"[WARN] Reading generated code may cause confidence.",
		"[WARN] Excessive elegance detected.",
		"[WARN] Humor subsystem active.",
		"[WARN] This comment has exceeded expectations.",
		"[WARN] Bison activity detected.",
		"[WARN] Source code appears unusually cooperative.",
		"[WARN] Developer may become attached to project.",
	];

    private static readonly string[] Discoveries =
	[
		"Researchers believe this artifact was generated by Appa.",
		"The purpose of this object remains unknown.",
		"Scholars remain divided on whether this is elegant.",
		"Several experts identified this as 'probably code.'",
		"Carbon dating suggests it was generated moments ago.",
		"The origin appears to be a machine known as Appa.",
		"Evidence suggests programmer involvement.",
		"Historians classify this as 'late-stage software.'",
		"The artifact appears to be fully domesticated.",
		"The meaning of the comments remains disputed.",
		"Analysts noted an unusual concentration of semicolons.",
		"The artifact shows clear signs of intentional structure.",
	];

    private static readonly string[] Quotes =
	[
		"Ship it.",
		"Looks good to me.",
		"We'll optimize it later.",
		"That's a problem for future us.",
		"Works on my machine.",
		"Send it.",
		"LGTM.",
		"Merge first, ask questions later.",
		"May the linker have mercy.",
		"We are go for compile.",
		"I've seen worse.",
		"Nobody touch anything.",
		"Deploy and act natural.",
		"Close enough.",
		"Let the CI worry about it.",
		"It compiled, ship it.",
		"Future me will handle this.",
		"I am not reading all that. Approved.",
	];

    private static readonly string[] Forecasts =
	[
		"Scattered semicolons with a chance of undefined behavior.",
		"Heavy allocation in the afternoon. Bring a GC.",
		"Overcast with intermittent type errors. Compile warm.",
		"Clear skies over the stack. Heap conditions uncertain.",
		"Mild recursion expected. Depth levels may vary.",
		"Dense fog in the linker region. Proceed with declarations.",
		"Isolated segfaults possible near midnight. Stay safe.",
		"100% chance of compilation. Results may vary.",
	];

    private static readonly string[] NightEntries =
	[
		"AST nominal. No anomalies detected.",
		"Build system stable. All processes accounted for.",
		"Parser still running. No cause for concern.",
		"Linker quiet. Suspiciously quiet.",
		"Memory usage within acceptable parameters. Mostly.",
		"Code generator operational. Output looks intentional.",
		"No dragons sighted. Monitoring continues.",
		"Transpilation complete. Notes: none. Status: fine.",
	];

    private static readonly string[] Absurdisms =
	[
		"A wild translation unit appears.",
		"The source code has unionized.",
		"This file was generated under adult supervision.",
		"This file was generated without adult supervision.",
		"A compiler somewhere is proud of you.",
		"A compiler somewhere is disappointed in you.",
		"The semicolons are free-range.",
		"The comments are locally sourced.",
		"This file passed customs inspection.",
		"Do not taunt the generated code.",
		"Keep away from open flames.",
		"Ask your compiler if Appa is right for you.",
		"Please remain seated until the build has completed.",
		"Objects in source code may be more abstract than they appear.",
		"The build system believes in you.",
		"The build system should not believe in you.",
		"Here be pointers.",
		"Abandon hope, all ye who grep here.",
		"The parser giveth and the parser taketh away.",
	];

    /// <summary>
    /// Returns a random element from the given array.
    /// </summary>
    private static string Pick(string[] values)
    {
        return values[_random.Next(values.Length)];
    }

    /// <summary>
    /// Returns a string of the given character repeated width times.
    /// </summary>
    private static string Sep(char c, int width)
    {
        return new string(c, width);
    }

    /// <summary>
    /// Returns a randomly chosen decorative header comment for the given output file name.
    /// One in 1000 calls returns the legendary header.
    /// </summary>
    public static string GenerateKewlHeader(string fileName)
	{
		if (_random.Next(1000) == 0) return LegendaryHeader(fileName);
		return _random.Next(16) switch
		{
			0  => Card(fileName),
			1  => Terminal(fileName),
			2  => AiAwakening(fileName),
			3  => AncientArtifact(fileName),
			4  => LoadingScreen(fileName),
			5  => Demoscene(fileName),
			6  => Propaganda(fileName),
			7  => SpaceMission(fileName),
			8  => ProgrammerThoughts(fileName),
			9  => Mythological(fileName),
			10 => Bureaucratic(fileName),
			11 => WeatherReport(fileName),
			12 => GameOver(fileName),
			13 => NightLog(fileName),
			14 => StatusBoard(fileName),
			_  => WarningLabel(fileName)
		};
	}

    /// <summary>
    /// Renders a boxed identity card whose width adapts to the longest content line.
    /// </summary>
    private static string Card(string fileName)
	{
		string tagline     = Pick(Taglines);
		string fact        = Pick(Facts);
		string observation = Pick(Observations);
		string[] rows =
		[
			$"File      : {fileName}",
			"Copyright : 2026 - u/ApparentlyPlus",
			$"Appa      : {tagline}",
			observation,
			fact,
		];
		int w = 60;
		for (int i = 0; i < rows.Length; i++)
			if (rows[i].Length > w) w = rows[i].Length;
		string bar   = Sep('═', w + 2);
		string blank = $"║{Sep(' ', w + 2)}║";
		string Row(string s) => $"║ {s.PadRight(w)} ║";
		return $"""
/*
╔{bar}╗
{blank}
{Row(rows[0])}
{Row(rows[1])}
{Row(rows[2])}
{blank}
{Row(rows[3])}
{Row(rows[4])}
╚{bar}╝
*/
""";
	}

    /// <summary>
    /// Renders a retro command-line terminal session header.
    /// </summary>
    private static string Terminal(string fileName)
    {
        return $"""
/*
 * C:\> appa transpile source.gata
 *
 * Parsing...
 * Building AST...
 * Doing mysterious compiler things...
 * Generating...
 *
 * Output:
 *     {fileName}
 *
 * Status:
 *     {Pick(Observations)}
 *
 * Build succeeded.
 */
""";
    }

    /// <summary>
    /// Renders a newly-conscious AI awakening header with randomized personality lines.
    /// </summary>
    private static string AiAwakening(string fileName)
    {
        return $"""
/*
 * {Pick(Greetings)}
 *
 * I am {fileName}.
 *
 * {Pick(AiLines)}
 * {Pick(AiLines)}
 *
 * Current emotional state: {Pick(EmotionalStates)}
 *
 * Please compile me gently.
 */
""";
    }

    /// <summary>
    /// Renders an ancient artifact discovery report whose separator scales to the file name length.
    /// </summary>
    private static string AncientArtifact(string fileName)
	{
		int w = Math.Max(fileName.Length + 16, 69);
		string sep = Sep('-', w);
		string discovery = Pick(Discoveries);
		string fact = Pick(Facts);
		return $"""
/*
 * {sep}
 *                     ANCIENT SOFTWARE ARTIFACT
 * {sep}
 *
 * Designation : {fileName}
 * Origin      : Earth
 * Species     : Human
 *
 * {discovery}
 *
 * {fact}
 *
 * {sep}
 */
""";
	}

    /// <summary>
    /// Renders a progress-bar loading screen header with randomly picked loading steps.
    /// </summary>
    private static string LoadingScreen(string fileName)
    {
        return $"""
/*
 * Loading {fileName}...
 *
 * [##############################] 100%
 *
 * {Pick(LoadingLines)}
 * {Pick(LoadingLines)}
 * {Pick(LoadingLines)}
 * {Pick(LoadingLines)}
 * {Pick(LoadingLines)}
 *
 * {Pick(Facts)}
 */
""";
    }

    /// <summary>
    /// Renders a demoscene-style ASCII art greeting header.
    /// </summary>
    private static string Demoscene(string fileName)
    {
        return $"""
/* 
 *               _.-````'-,_
 *   _,.,_ ,-'`           `'-.,_
 * /)     (\                   '``-.
 * ((      ) )                      `\
 * \)    (_/                        )\
 *  |       /)           ' ,   ,'    / \
 *  `\    ^'            '     (    /  ))
 *    |      _/\ ,     /    ,,`\   (  "`
 *     \Y,   |  \  \  | ````| / \_ \
 *       `)_/    \  \  )    ( >  ( >
 *                \( \(     |/   |/
 *               /_(/_(    /_(  /_(
 *
 * FILE: {fileName}
 *
 * Greetings to all parsers,
 * all linker enjoyers,
 * and all keepers of ancient build scripts.
 *
 * {Pick(Observations)}
 */
""";
    }

    /// <summary>
    /// Renders a totalitarian build-system propaganda header.
    /// </summary>
    private static string Propaganda(string fileName)
    {
        return $"""
/*
 * ATTENTION CITIZEN
 *
 * FILE IDENTIFIER:
 *     {fileName}
 *
 * THIS FILE HAS BEEN GENERATED
 * FOR THE GLORY OF THE BUILD SYSTEM.
 *
 * PRODUCTIVITY HAS INCREASED 12%.
 *
 * {Pick(Facts)}
 */
""";
    }

    /// <summary>
    /// Renders a NASA mission control go/no-go poll header.
    /// </summary>
    private static string SpaceMission(string fileName)
    {
        return $"""
/*
 * APPA MISSION CONTROL
 *
 * Vessel:
 *     {fileName}
 *
 * Parser:          GO
 * Code Generator:  GO
 * Linker:          GO
 *
 * Flight Director:
 *     "{Pick(Quotes)}"
 */
""";
    }

    /// <summary>
    /// Renders a self-aware file comment where the file reflects on knowing its own name.
    /// </summary>
    private static string ProgrammerThoughts(string fileName)
    {
        return $"""
/*
 * Fun fact:
 *
 * This file knows its own name.
 *
 * It is:
 *
 *     {fileName}
 *
 * The file is very proud of this achievement.
 *
 * Please clap.
 */
""";
    }

    /// <summary>
    /// Renders a mythological hero's journey header where source code plays the hero.
    /// </summary>
    private static string Mythological(string fileName)
    {
        return $"""
/*
 * In ancient times,
 * Appa carried heroes across impossible distances.
 *
 * Today it carries source code.
 *
 * Destination:
 *     {fileName}
 *
 * {Pick(Facts)}
 *
 * The linker shall decide its fate.
 */
""";
    }

    /// <summary>
    /// Renders an official government-form bureaucratic header.
    /// </summary>
    private static string Bureaucratic(string fileName)
    {
        return $"""
/*
 * FORM C-417-B  (Rev. 2026)
 * APPLICATION FOR GENERATED FILE EXISTENCE
 * {Sep('=', 40)}
 *
 * Applicant      : Appa Compiler, Unincorporated
 * File Name      : {fileName}
 * Purpose        : Translation unit
 * Justification  : Source code required C output
 *
 * This form has been reviewed and approved by no one in particular.
 *
 * NOTICE    : {Pick(Facts)}
 * Section 7c: {Pick(Observations)}
 *
 * Signed,
 *     The Build System
 */
""";
    }

    /// <summary>
    /// Renders a local weather forecast header themed around compiler conditions.
    /// </summary>
    private static string WeatherReport(string fileName)
    {
        return $"""
/*
 * APPA METEOROLOGICAL SERVICE
 * Local Forecast for: {fileName}
 * {Sep('=', Math.Max(fileName.Length + 20, 44))}
 *
 * TODAY:
 *     {Pick(Forecasts)}
 *
 * TONIGHT:
 *     {Pick(Forecasts)}
 *
 * EXTENDED OUTLOOK:
 *     Compilation expected to succeed. Eventually.
 *
 * {Pick(Observations)}
 */
""";
    }

    /// <summary>
    /// Renders a retro arcade game-over / continue screen header.
    /// </summary>
    private static string GameOver(string fileName)
    {
        return $"""
/*
 *  ██████╗  █████╗ ███╗   ███╗███████╗     ██████╗ ██╗   ██╗███████╗██████╗
 * ██╔════╝ ██╔══██╗████╗ ████║██╔════╝    ██╔═══██╗██║   ██║██╔════╝██╔══██╗
 * ██║  ███╗███████║██╔████╔██║█████╗      ██║   ██║██║   ██║█████╗  ██████╔╝
 * ██║   ██║██╔══██║██║╚██╔╝██║██╔══╝      ██║   ██║╚██╗ ██╔╝██╔══╝  ██╔══██╗
 * ╚██████╔╝██║  ██║██║ ╚═╝ ██║███████╗    ╚██████╔╝ ╚████╔╝ ███████╗██║  ██║
 *  ╚═════╝ ╚═╝  ╚═╝╚═╝     ╚═╝╚══════╝     ╚═════╝   ╚═══╝  ╚══════╝╚═╝  ╚═╝
 *
 * FILE: {fileName}
 *
 * CONTINUE?   9 ... 8 ... 7 ...
 *
 * INSERT COIN TO LINK
 *
 * {Pick(Quotes)}
 */
""";
    }

    /// <summary>
    /// Renders a night-shift operator log header with randomly drawn status entries.
    /// </summary>
    private static string NightLog(string fileName)
    {
        return $"""
/*
 * APPA NIGHT SHIFT OPERATIONS LOG
 * {Sep('=', 32)}
 * Unit     : {fileName}
 * Shift    : Compilation
 * Operator : Appa Compiler v1.0
 *
 * [00:00] Shift started.
 * [00:01] {Pick(NightEntries)}
 * [00:02] {Pick(NightEntries)}
 * [00:03] File generated successfully.
 * [00:04] Shift complete. Handing off to linker.
 *
 * Notes: {Pick(Facts)}
 */
""";
    }

    /// <summary>
    /// Renders a build status board header whose separator scales to the file name length.
    /// </summary>
    private static string StatusBoard(string fileName)
	{
		int w   = Math.Max(fileName.Length + 8, 50);
		string sep = Sep('=', w);
		return $"""
/*
 * {sep}
 *  APPA BUILD STATUS REPORT
 *  File: {fileName}
 * {sep}
 *
 * {Pick(StatusLines)}
 * {Pick(StatusLines)}
 * {Pick(StatusLines)}
 * {Pick(StatusLines)}
 * {Pick(StatusLines)}
 *
 * {Pick(Absurdisms)}
 * {sep}
 */
""";
	}

    /// <summary>
    /// Renders a warning-label header with random compiler advisory notices.
    /// </summary>
    private static string WarningLabel(string fileName)
    {
        return $"""
/*
 * FILE: {fileName}
 *
 * {Pick(WarningLines)}
 * {Pick(WarningLines)}
 * {Pick(WarningLines)}
 *
 * {Pick(Absurdisms)}
 */
""";
    }

    /// <summary>
    /// Renders the legendary header. Appears with 0.1% probability.
    /// </summary>
    private static string LegendaryHeader(string fileName)
	{
		int w   = Math.Max(fileName.Length + 14, 68);
		string bar = Sep('*', w);
		return $"""
/*
 * {bar}
 * CONGRATULATIONS!
 *
 * You have discovered a legendary Appa header.
 *
 * File        : {fileName}
 * Probability : 0.1%
 *
 * There is no reward.
 *
 * The reward was the header.
 *
 * {Pick(Absurdisms)}
 * {bar}
 */
""";
	}
}
