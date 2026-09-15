// ============================================================================
// LLM Mechanics — entry point
//
// Every phase lives in its own StepXxx class and is a self-contained harness
// with its own client, CSV writer and argument parsing. The duplication is on
// purpose: a harness is a measurement instrument, and a change made for a later
// phase must not silently change how an earlier phase measured.
//
// The first argument selects the step; everything after it is handed to that
// step untouched:
//   dotnet run -- 1 --model qwen/qwen3.5-9b 2 4
//   dotnet run -- 2 --model qwen/qwen3.5-9b --n 20 --temp 0.7
//
// Adding a step:
//   1. StepThree.cs: `static class StepThree` with
//      `public static async Task<int> RunAsync(string[] args)` as its entry
//      point. Helper types (LmClient, Config, Csv, ...) go INSIDE the class,
//      otherwise they collide with the other steps' types of the same name.
//   2. One row in the table below.
// ============================================================================

using LlmMechanics;

(string Id, string Title, Func<string[], Task<int>> Run)[] steps =
[
    ("1", "Local inference measurements", StepOne.RunAsync),
    ("2", "Structured output", StepTwo.RunAsync),
];

var step = args.Length > 0 ? steps.FirstOrDefault(s => s.Id == args[0]) : default;

if (step.Run is null)
{
    Console.Error.WriteLine(args.Length == 0 ? "No step selected." : $"Unknown step: '{args[0]}'");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage: dotnet run -- <step> [step arguments]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Steps:");
    foreach (var s in steps) Console.Error.WriteLine($"  {s.Id}  {s.Title}");
    return 1;
}

return await step.Run(args[1..]);
