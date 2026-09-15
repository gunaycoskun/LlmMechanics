// ============================================================================
// Phase 1 — LLM Mechanics Measurement Harness v2 (Local / LM Studio)
//
// Bug found in v1: Qwen3.5 is a reasoning model. It emits thinking tokens in
// delta.reasoning_content, NOT in delta.content. Because v1 only looked at
// content:
//   - all outputs appeared empty (Experiments 3 and 6)
//   - the TTFT stamp landed at the END of generation, ttft == total (Exp. 2 and 4)
//
// Fixed in v2:
//   1. reasoning_content is read as a separate channel (stream + non-stream)
//   2. Two distinct TTFTs: TtftAny (physical prefill) / TtftContent (perceived
//      latency)
//   3. Three ways of turning thinking off are ATTEMPTED and the outcome is
//      reported in the pre-flight check: chat_template_kwargs.enable_thinking,
//      the /no_think system message, and (on the LM Studio side) Reasoning
//      Budget. On qwen3.5-9b ALL THREE ARE INEFFECTIVE — the code detects this
//      and runs the measurements with thinking enabled.
//   4. Experiment 2 arm A max_tokens 16 -> 64 (16 left no headroom)
//   5. Experiment 4 moved to a long context (with a short prompt the KV cache
//      effect was buried in noise)
//   6. A concurrency level of 16 was added to Experiment 5 (the curve had not
//      saturated)
//   7. Experiment 6 runs two arms if thinking can be disabled, one arm if not
//
// No dependencies. We use a raw HttpClient because an abstraction would
// normalize exactly the provider differences we want to measure.
//
// Running it (the first argument, 1, selects this step — see Program.cs):
//   dotnet run -- 1 --model qwen/qwen3.5-9b
//   dotnet run -- 1 --model qwen/qwen3.5-9b 2 3 4
//   dotnet run -- 1 --url http://localhost:1234/v1 --out results2 --doc-words 2000
// ============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmMechanics;

static class StepOne
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        Config config;
        try
        {
            config = Config.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Config.Usage);
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var client = new LmClient(http, config.BaseUrl, config.ModelId);

        try
        {
            Directory.CreateDirectory(config.OutputDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: could not create the output directory ({config.OutputDir}) — {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Base URL : {config.BaseUrl}");
        Console.WriteLine($"Model    : {(string.IsNullOrWhiteSpace(config.ModelId) ? "(auto-discovery)" : config.ModelId)}");

        if (string.IsNullOrWhiteSpace(config.ModelId))
        {
            List<string> models;
            try
            {
                models = await client.ListModelsAsync();
            }
            catch (Exception ex)
            {
                // This exception used to go uncaught: with LM Studio closed the program
                // died with a stack trace and the helpful message below never appeared.
                Console.Error.WriteLine($"ERROR: could not fetch the model list — {ex.Message}");
                Console.Error.WriteLine($"       Server address: {config.BaseUrl}");
                Console.Error.WriteLine("       LM Studio > Developer > Status: Running, and a model must be loaded.");
                return 1;
            }

            if (models.Count == 0)
            {
                Console.Error.WriteLine("ERROR: no models on the server. LM Studio > Developer > Status: Running, and a model must be loaded.");
                return 1;
            }

            Console.WriteLine("\nModels found:");
            foreach (var m in models) Console.WriteLine($"  - {m}");

            // Filtering out only 'embed' was not enough; non-chat models such as
            // rerank/whisper passed the filter and could be selected silently.
            string[] notChat = ["embed", "rerank", "whisper", "clip", "vae", "tts"];

            var chatCandidates = models
                .Where(m => !notChat.Any(t => m.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (chatCandidates.Count == 0)
            {
                Console.Error.WriteLine("ERROR: no chat model found (they all look like embedding/rerank models).");
                Console.Error.WriteLine("       Name one explicitly with --model.");
                return 1;
            }

            if (chatCandidates.Count > 1)
                Console.WriteLine($"WARNING: {chatCandidates.Count} chat candidates found, picking the first. " +
                                  "Use --model for consistency across runs.");

            client.ModelId = chatCandidates[0];
            Console.WriteLine($"\nSelected chat model: {client.ModelId}");
        }

        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Console.WriteLine($"Run ID   : {runId}");

        // ----------------------------------------------------------------------------
        // PRE-FLIGHT CHECK — reasoning channel and enable_thinking support
        //
        // We verify this BEFORE measuring. Every v1 bug came from "I assumed".
        // ----------------------------------------------------------------------------

        // Warm-up BEFORE the pre-flight check: the first call absorbs model loading,
        // CUDA context init and kernel compilation. If the pre-flight check ran first,
        // the ttftAny it prints would reflect that cold cost (measured difference:
        // 2413 ms versus 65 ms) and the user would read a wrong baseline.
        Console.WriteLine("\nWarm-up (3 calls, excluded from measurements)...");
        try
        {
            for (var i = 0; i < 3; i++)
                await client.ChatAsync([new Message("user", "Hello.")], 16, 0, disableThinking: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nERROR: warm-up call failed — {ex.Message}");
            Console.Error.WriteLine($"       The server may be unreachable: {config.BaseUrl}");
            Console.Error.WriteLine("       LM Studio > Developer > Status: Running, and is a model loaded?");
            return 1;
        }
        Console.WriteLine("Warm-up done.");

        Header("PRE-FLIGHT CHECK — reasoning channel and enable_thinking");

        const string probePrompt = "What is a progress payment? One sentence.";

        StreamResult probeOn, probeOff;
        try
        {
            probeOn = await client.ChatStreamAsync(
                [new Message("user", probePrompt)], maxTokens: 300, temperature: 0);

            Console.WriteLine($"Thinking ON  : content={probeOn.Text.Length} chars, " +
                              $"reasoning={probeOn.Reasoning.Length} chars, " +
                              $"reasoning_tokens={probeOn.ReasoningTokens}, " +
                              $"ttftAny={Fmt(probeOn.TtftAnyMs)}, ttftContent={Fmt(probeOn.TtftContentMs)}");

            probeOff = await client.ChatStreamAsync(
                [new Message("user", probePrompt)], maxTokens: 300, temperature: 0,
                disableThinking: true);

            Console.WriteLine($"Thinking OFF : content={probeOff.Text.Length} chars, " +
                              $"reasoning={probeOff.Reasoning.Length} chars, " +
                              $"reasoning_tokens={probeOff.ReasoningTokens}, " +
                              $"ttftAny={Fmt(probeOff.TtftAnyMs)}, ttftContent={Fmt(probeOff.TtftContentMs)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nERROR: pre-flight call failed — {ex.Message}");
            Console.Error.WriteLine($"       The server may be unreachable: {config.BaseUrl}");
            Console.Error.WriteLine("       LM Studio > Developer > Status: Running, and is a model loaded?");
            return 1;
        }

        // Some servers emit reasoning inside content as <think>...</think> rather than
        // in a separate field. Without this check we would wrongly conclude "thinking
        // is off", stamp ttftAny on the first content token and pollute every coefficient.
        var inlineThink = probeOff.Text.Contains("<think", StringComparison.OrdinalIgnoreCase);

        if (inlineThink)
            Console.WriteLine("WARNING: a <think> block was seen inside content — reasoning is not arriving on a separate channel.");

        var thinkingOff = probeOff.Reasoning.Length == 0 && probeOff.Text.Length > 0 && !inlineThink;
        client.CanDisableThinking = thinkingOff;

        if (!thinkingOff)
        {
            Console.WriteLine();
            Console.WriteLine("WARNING: enable_thinking=false has no effect. Trying the /no_think fallback...");

            // CAREFUL: this once read `= false`; since the property already defaults to
            // false the fallback never engaged and the probe below repeated the exact
            // same request — meaning "the fallback did not work" was concluded from a
            // path that was never taken.
            client.UseNoThinkFallback = true;

            var probeOff2 = await client.ChatStreamAsync(
                [new Message("user", probePrompt)], maxTokens: 300, temperature: 0,
                disableThinking: true);

            Console.WriteLine($"With fallback: content={probeOff2.Text.Length} chars, " +
                              $"reasoning={probeOff2.Reasoning.Length} chars");

            var fallbackWorks = probeOff2.Reasoning.Length == 0 && probeOff2.Text.Length > 0
                                && !probeOff2.Text.Contains("<think", StringComparison.OrdinalIgnoreCase);

            client.CanDisableThinking = fallbackWorks;

            if (fallbackWorks)
            {
                Console.WriteLine("       The /no_think fallback WORKS and will be used in the measurements.");
                Console.WriteLine("       Note: it adds a fixed system turn to every prompt and inflates");
                Console.WriteLine("       Experiment 1's token counts on both sides (pushing the ratio toward 1).");
            }
            else
            {
                // Roll it back. Left enabled it adds a fixed system turn to every prompt
                // for no benefit — that is exactly what distorted Experiment 1's ratio.
                client.UseNoThinkFallback = false;
                Console.WriteLine("       The fallback did not work either; rolled back. Measurements will run with thinking ON.");
                Console.WriteLine("       Add this note to the README — the coefficients include reasoning.");
            }
        }

        if (probeOn.Text.Length == 0 && probeOn.Reasoning.Length == 0)
        {
            Console.Error.WriteLine("\nERROR: the model produced no content at all. Is a model loaded, is the context setting correct?");
            return 1;
        }

        // ----------------------------------------------------------------------------
        // Note: the warm-up above runs BEFORE the pre-flight check — the first calls
        // include model loading, CUDA context init and kernel compilation. On Gemini
        // this cost did not exist (the server was already warm); locally it is the
        // single largest source of outliers.
        // ----------------------------------------------------------------------------

        // Each experiment runs in its own try/catch: Experiment 2 makes 50 and
        // Experiment 3 makes 80 sequential calls; a single 500 throwing away the whole
        // run and preventing later experiments from running burned measurement hours.
        var sel = config.Experiments;
        var failedExperiments = new List<int>();

        if (sel.Contains(1)) await Run(1, () => Experiment1_Tokenization(client, config, runId));
        if (sel.Contains(2)) await Run(2, () => Experiment2_PrefillDecode(client, config, runId));
        if (sel.Contains(3)) await Run(3, () => Experiment3_Determinism(client, config, runId));
        if (sel.Contains(4)) await Run(4, () => Experiment4_KvCache(client, config, runId));
        if (sel.Contains(5)) await Run(5, () => Experiment5_Concurrency(client, config, runId));
        if (sel.Contains(6)) await Run(6, () => Experiment6_QualitySpotCheck(client, config, runId));

        async Task Run(int no, Func<Task> body)
        {
            try
            {
                await body();
            }
            catch (Exception ex)
            {
                failedExperiments.Add(no);
                Console.Error.WriteLine($"\nERROR: Experiment {no} aborted — {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine("       No CSV was written for this experiment. Continuing with the next ones.");
            }
        }

        Console.WriteLine($"\nDone. Outputs: {Path.GetFullPath(config.OutputDir)}");

        if (failedExperiments.Count > 0)
        {
            Console.Error.WriteLine($"WARNING: failed experiments: {string.Join(", ", failedExperiments)}");
            return 2;
        }

        return 0;
    }


    // ============================================================================
    // EXPERIMENT 1 — Tokenization: the Turkish token penalty
    //
    // On Gemini, Turkish consumed 1.33x the tokens of English (BPE vocabulary
    // bias). Qwen was trained on multilingual data; we expect a lower multiplier.
    //
    // INTERPRETATION WARNING: prompt_tokens also includes the chat template
    // overhead (~15-20 tokens, constant on both sides). Because a constant term
    // inflates both numerator and denominator, the measured ratio comes out LOWER
    // than the true in-text ratio. This experiment gives a lower bound.
    //
    // Why it matters: public-procurement / progress-payment documents are in
    // Turkish. The multiplier determines how many pages of contract fit in the
    // context and how large a chunk can be.
    // ============================================================================
    static async Task Experiment1_Tokenization(LmClient client, Config config, string runId)
    {
        Header("EXPERIMENT 1 — Tokenization: the Turkish token penalty");

        // The Turkish side is measurement data, NOT user-facing text: this
        // experiment exists to compare Turkish against English tokenization, so
        // translating it would collapse the ratio to ~1.0 and destroy the
        // experiment. Leave these strings in Turkish.
        var pairs = new (string Name, string Tr, string En)[]
        {
            ("short_sentence",
             "Yüklenici, hakediş raporunu idareye sunmakla yükümlüdür.",
             "The contractor is obliged to submit the progress payment report to the administration."),

            ("technical_paragraph",
             "İş artışı halinde, sözleşme bedelinin yüzde yirmisine kadar olan kısım için " +
             "yükleniciye ek süre verilir ve fiyat farkı hesaplaması güncel endeks değerleri " +
             "üzerinden yapılır. İdare, bu kararı gerekçeleriyle birlikte yazılı olarak bildirir.",
             "In case of a work increase, additional time is granted to the contractor for the portion " +
             "up to twenty percent of the contract price, and the price difference calculation is made " +
             "based on current index values. The administration notifies this decision in writing with its justification."),

            ("list",
             "Belgeler: teklif mektubu, geçici teminat, iş deneyim belgesi, vergi borcu yoktur yazısı.",
             "Documents: bid letter, bid bond, work experience certificate, tax clearance letter."),

            ("numeric",
             "Sözleşme bedeli 12.450.000,00 TL olup, ilk hakediş tutarı 1.245.000,00 TL'dir.",
             "The contract price is 12,450,000.00 TRY and the first progress payment is 1,245,000.00 TRY."),

            ("long_clause",
             "Sözleşmenin uygulanması sırasında ortaya çıkan ve yüklenicinin kusurundan " +
             "kaynaklanmayan gecikmelerde, idare tarafından yükleniciye süre uzatımı verilebilir. " +
             "Süre uzatımı talebi, gecikmeye neden olan olayın sona ermesinden itibaren yirmi gün " +
             "içinde yazılı olarak idareye bildirilir ve gerekçeleri belgelendirilir.",
             "During the execution of the contract, in case of delays not arising from the contractor's " +
             "fault, the administration may grant an extension of time to the contractor. The request " +
             "for extension of time shall be submitted to the administration in writing within twenty " +
             "days from the end of the event causing the delay, and its justifications shall be documented."),
        };

        // --- Template constant calibration ---
        // The "~15-20 tokens" in the comment was an assumption that had never been
        // MEASURED. We send a single-character prompt and read prompt_tokens:
        // everything left over is chat template overhead. Since that constant
        // inflates both numerator and denominator it pushes the measured ratio
        // toward 1; calling it a "lower bound" without measuring it was not enough.
        var calib = await client.ChatAsync([new Message("user", ".")], 1, 0, disableThinking: true);
        var templateOverhead = Math.Max(0, calib.PromptTokens - 1);

        Console.WriteLine($"Template constant (measured): {templateOverhead} tokens " +
                          $"(single-character prompt = {calib.PromptTokens} prompt_tokens)");
        Console.WriteLine();

        var rows = new List<string[]>();

        // The summary statistics used to be parsed back out of the STRINGS written
        // to the CSV using fixed indices; when a column was added the indices
        // shifted and the average of the wrong column was reported without a
        // compiler warning. We keep the measurements typed instead.
        var measured = new List<(double Ratio, double NetRatio, int TrTok, int EnTok)>();

        Console.WriteLine($"{"text",-18} {"TR tok",8} {"EN tok",8} {"ratio",8} {"net ratio",9} {"TR chr",8} {"EN chr",8}");
        Console.WriteLine(new string('-', 72));

        foreach (var (name, tr, en) in pairs)
        {
            // max_tokens=1 + thinking off: generation cost is minimal,
            // we only care about prompt_tokens.
            var trRes = await client.ChatAsync([new Message("user", tr)], 1, 0, disableThinking: true);
            var enRes = await client.ChatAsync([new Message("user", en)], 1, 0, disableThinking: true);

            if (trRes.PromptTokens == 0 || enRes.PromptTokens == 0)
                throw new InvalidOperationException(
                    $"prompt_tokens came back 0 for '{name}' — the server is not sending the usage field, " +
                    "which would make every ratio in this experiment meaningless.");

            var ratio = (double)trRes.PromptTokens / enRes.PromptTokens;

            // Ratio with the template constant removed: this is the real in-text penalty.
            var trNet = Math.Max(1, trRes.PromptTokens - templateOverhead);
            var enNet = Math.Max(1, enRes.PromptTokens - templateOverhead);
            var netRatio = (double)trNet / enNet;

            Console.WriteLine($"{name,-18} {trRes.PromptTokens,8} {enRes.PromptTokens,8} {ratio,8:F3} " +
                              $"{netRatio,9:F3} {tr.Length,8} {en.Length,8}");

            rows.Add([
                runId, client.ModelId, name,
                trRes.PromptTokens.ToString(), enRes.PromptTokens.ToString(),
                Inv(ratio, "F4"),
                tr.Length.ToString(), en.Length.ToString(),
                Inv((double)tr.Length / trRes.PromptTokens, "F3"),
                Inv((double)en.Length / enRes.PromptTokens, "F3"),
                templateOverhead.ToString(),
                trNet.ToString(), enNet.ToString(), Inv(netRatio, "F4"),
            ]);

            measured.Add((ratio, netRatio, trRes.PromptTokens, enRes.PromptTokens));
        }

        // Three different aggregations, each answering a different question.
        // Reporting a single number left it ambiguous which one was meant.
        var avg = measured.Average(m => m.Ratio);                                            // macro
        var max = measured.Max(m => m.Ratio);
        var trTotal = measured.Sum(m => m.TrTok);
        var enTotal = measured.Sum(m => m.EnTok);
        var micro = (double)trTotal / Math.Max(enTotal, 1);                                  // token weighted
        var netAvg = measured.Average(m => m.NetRatio);                                      // template removed
        var netMax = measured.Max(m => m.NetRatio);

        Console.WriteLine(new string('-', 72));
        Console.WriteLine($"Raw ratio     : mean {avg:F3} | worst {max:F3} | token-weighted {micro:F3}");
        Console.WriteLine($"Net ratio     : mean {netAvg:F3} | worst {netMax:F3}   (template constant removed)");
        Console.WriteLine("Gemini reference: 1.33. Size your chunks by the WORST CASE, not the");
        Console.WriteLine("MEAN — term-dense lists are the most expensive kind of text.");
        Console.WriteLine("Use the NET worst case when planning chunks: that is the real in-text penalty.");

        Csv.Write(config, "01_tokenization",
            ["run_id", "model", "text", "tr_tokens", "en_tokens", "ratio",
             "tr_chars", "en_chars", "tr_chars_per_token", "en_chars_per_token",
             "template_overhead_tokens", "tr_net_tokens", "en_net_tokens", "net_ratio"],
            rows);
    }


    // ============================================================================
    // EXPERIMENT 2 — Prefill / decode coefficient decomposition
    //
    // On Gemini the model was: ~1200ms constant + ~3ms/token. That constant cost
    // was network + queueing. Locally there is no network. In its place there are
    // two distinct physical processes:
    //
    //   prefill : input tokens are processed IN PARALLEL (compute-bound, cheap)
    //   decode  : output tokens are produced SEQUENTIALLY (memory-bandwidth-bound,
    //             expensive)
    //
    // The right model:  total ~= c + a*input_tokens + b*output_tokens
    //
    // v1 bug: it computed b from (total - ttft); because ttft == total it was
    // dividing by zero. v2 regresses total directly on output_tokens — more
    // robust, and independent of ttft.
    //
    // Why it matters: it is the numeric answer to "should I shorten the prompt or
    // the output".
    // ============================================================================
    static async Task Experiment2_PrefillDecode(LmClient client, Config config, string runId)
    {
        Header("EXPERIMENT 2 — Prefill / decode decomposition");

        const int reps = 5;
        const int aOutTokens = 64;

        var rows = new List<string[]>();
        var inputSizes = new[] { 1, 20, 100, 400, 1200 };      // approximate word count
        var outputSizes = new[] { 32, 64, 128, 256, 512 };     // tokens

        const double aTemp = 0.0;
        const double bTemp = 0.7;

        // Measurement point -> row. Columns 3..7 are fixed because the regression
        // below indexes them; new columns are appended AT THE END.
        void AddRow(string arm, double temp, List<StreamResult> samples, double inTok, double outTok)
        {
            // Samples whose ttftAny could not be measured (no chunk was parsed) are
            // left out; folding them down to totalMs and mixing them into the mean
            // silently reintroduced v1's ttft==total bug.
            var ttfts = samples.Where(s => s.TtftAnyMs >= 0).Select(s => s.TtftAnyMs).ToList();
            var ttft = ttfts.Count > 0 ? Stats.Median(ttfts) : -1;

            rows.Add([
                runId, client.ModelId, arm, Inv(inTok, "F1"), Inv(outTok, "F1"),
                Inv(ttft, "F2"),
                Inv(Stats.Median(samples.Select(s => s.TotalMs)), "F2"),
                Inv(Stats.Percentile(samples.Select(s => s.TotalMs), 95), "F2"),
                Inv(temp, "F2"),
                samples.Count.ToString(),
                ttfts.Count.ToString(),
                samples.Min(s => s.CompletionTokens).ToString(),
                samples.Max(s => s.CompletionTokens).ToString(),
                samples.Count(s => s.CompletionTokensEstimated).ToString(),
                samples.Count(s => s.FinishReason == "length").ToString(),
            ]);
        }

        Console.WriteLine($"Arm A — input VARIES, output fixed ({aOutTokens} token ceiling), temperature={aTemp}:");
        Console.WriteLine($"{"in_tok",8} {"out_tok",8} {"out±",8} {"ttftAny",10} {"total_p50",10} {"total_p95",10}");
        Console.WriteLine(new string('-', 60));

        foreach (var size in inputSizes)
        {
            var samples = new List<StreamResult>();
            for (var i = 0; i < reps; i++)
            {
                // Unique prefix: we BREAK the KV cache hit on purpose.
                // Without it, prefill is skipped from the second call onward and we
                // would measure a fake speed-up. The cache effect is measured
                // DELIBERATELY in Experiment 4.
                var prompt = $"[{Guid.NewGuid():N}] " + Filler(size) + "\nSummarize this text in a single word.";
                samples.Add(await client.ChatStreamAsync(
                    [new Message("user", prompt)], aOutTokens, aTemp, disableThinking: true));
            }

            var ttftVals = samples.Where(s => s.TtftAnyMs >= 0).Select(s => s.TtftAnyMs).ToList();
            var ttft = ttftVals.Count > 0 ? Stats.Median(ttftVals) : -1;
            var p50 = Stats.Median(samples.Select(s => s.TotalMs));
            var p95 = Stats.Percentile(samples.Select(s => s.TotalMs), 95);
            var inTok = samples.Average(s => (double)s.PromptTokens);
            var outTok = samples.Average(s => (double)s.CompletionTokens);
            var outSpread = samples.Max(s => s.CompletionTokens) - samples.Min(s => s.CompletionTokens);

            Console.WriteLine($"{inTok,8:F0} {outTok,8:F1} {outSpread,8} {Fmt(ttft),10} {p50,10:F1} {p95,10:F1}");

            AddRow("A_prefill", aTemp, samples, inTok, outTok);
        }

        Console.WriteLine($"\nArm B — input fixed (short), output VARIES, temperature={bTemp}:");
        Console.WriteLine($"{"in_tok",8} {"out_tok",8} {"ttftAny",10} {"total_p50",10} {"tok/s",10}");
        Console.WriteLine(new string('-', 50));

        foreach (var size in outputSizes)
        {
            var samples = new List<StreamResult>();
            for (var i = 0; i < reps; i++)
            {
                var prompt = $"[{Guid.NewGuid():N}] Write a long text about construction site safety.";
                samples.Add(await client.ChatStreamAsync(
                    [new Message("user", prompt)], size, bTemp, disableThinking: true));
            }

            var ttftVals = samples.Where(s => s.TtftAnyMs >= 0).Select(s => s.TtftAnyMs).ToList();
            var ttft = ttftVals.Count > 0 ? Stats.Median(ttftVals) : -1;
            var p50 = Stats.Median(samples.Select(s => s.TotalMs));
            var outTok = samples.Average(s => (double)s.CompletionTokens);
            var inTok = samples.Average(s => (double)s.PromptTokens);

            // Division guard: if ttft coincides with total (the v1 bug) this would
            // print infinity.
            var span = ttft >= 0 ? p50 - ttft : -1;
            var tpsText = span > 1.0 ? $"{outTok / (span / 1000.0):F1}" : "n/a";

            Console.WriteLine($"{inTok,8:F0} {outTok,8:F1} {Fmt(ttft),10} {p50,10:F1} {tpsText,10}");

            AddRow("B_decode", bTemp, samples, inTok, outTok);
        }

        // --- Coefficient extraction ---
        // b (decode): regress total on output_tokens in arm B.
        // a (prefill): regress on input_tokens in arm A — TWICE.
        //
        //   a_total : from total_p50 (the old behaviour, kept for comparability)
        //   a_ttft  : from ttft_any_p50 (the clean measurement)
        //
        // Why they differ: in arm A max_tokens is a CEILING, not a fixed value. If
        // the output length moves with the input, the decode term leaks into the
        // total-based slope (omitted-variable bias). ttftAny stamps the moment
        // prefill finishes and is completely independent of decode — it is the
        // right instrument for the prefill coefficient. A large gap between the two
        // means the output length was not constant.

        var aRows = rows.Where(r => r[2] == "A_prefill").ToList();
        var bRows = rows.Where(r => r[2] == "B_decode").ToList();

        static double Col(string[] r, int i) => double.Parse(r[i], CultureInfo.InvariantCulture);

        var bX = bRows.Select(r => Col(r, 4)).ToArray();
        var bY = bRows.Select(r => Col(r, 6)).ToArray();
        var (b, bIntercept) = Stats.LinearFit(bX, bY);
        var bR2 = Stats.RSquared(bX, bY, b, bIntercept);

        var aX = aRows.Select(r => Col(r, 3)).ToArray();
        var aYtotal = aRows.Select(r => Col(r, 6)).ToArray();
        var (aTotal, aTotalIntercept) = Stats.LinearFit(aX, aYtotal);
        var aTotalR2 = Stats.RSquared(aX, aYtotal, aTotal, aTotalIntercept);

        var ttftRows = aRows.Where(r => Col(r, 5) >= 0).ToList();
        var aTtftX = ttftRows.Select(r => Col(r, 3)).ToArray();
        var aTtftY = ttftRows.Select(r => Col(r, 5)).ToArray();
        var (aTtft, aTtftIntercept) = Stats.LinearFit(aTtftX, aTtftY);
        var aTtftR2 = Stats.RSquared(aTtftX, aTtftY, aTtft, aTtftIntercept);

        // We use the ttft-based value as the prefill coefficient; if it could not be
        // measured (no ttft at all) we fall back to the old one and say so plainly.
        var aFromTtft = aTtftX.Length >= 2 && aTtft > 0;
        var a = aFromTtft ? aTtft : aTotal;

        var aAvgOut = aRows.Average(r => Col(r, 4));
        var pureOverhead = aTotalIntercept - b * aAvgOut;
        var ratio = a > 1e-9 ? b / a : double.NaN;

        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"Prefill (a) : {a:F4} ms / input token   [{(aFromTtft ? "ttft based" : "total based — no ttft")}]");
        Console.WriteLine($"   a_ttft   : {aTtft:F4}  (R²={aTtftR2:F3}, n={aTtftX.Length})");
        Console.WriteLine($"   a_total  : {aTotal:F4}  (R²={aTotalR2:F3}, n={aX.Length})  <- old method");
        Console.WriteLine($"Decode  (b) : {b:F3} ms / output token   (~{(b > 0 ? 1000 / b : 0):F1} tok/s, R²={bR2:F3})");
        Console.WriteLine($"Constant (c): {pureOverhead:F0} ms   (arm B intercept: {bIntercept:F0} ms)");
        Console.WriteLine();

        // Guard against silent nonsense. It used to write "ratio = 0" to the CSV
        // when a<=0, and that read like a measured result.
        var suspect = new List<string>();
        if (a <= 1e-9) suspect.Add($"prefill coefficient is not positive (a={a:F5})");
        if (b <= 1e-9) suspect.Add($"decode coefficient is not positive (b={b:F5})");
        if (pureOverhead < 0) suspect.Add($"constant overhead is negative ({pureOverhead:F0} ms) — b is borrowed from a different regime");
        if (aTtftR2 < 0.9 && aTtftX.Length >= 2) suspect.Add($"weak prefill fit (R²={aTtftR2:F3})");
        if (bR2 < 0.9) suspect.Add($"weak decode fit (R²={bR2:F3})");
        if (Math.Abs(aTotal - aTtft) > 0.5 * Math.Max(aTtft, 1e-9) && aFromTtft)
            suspect.Add("a_total and a_ttft diverge by more than 50% — output length is not constant in arm A");

        if (double.IsNaN(ratio))
            Console.WriteLine(">>> out/in cost ratio COULD NOT BE COMPUTED (a <= 0) <<<");
        else
            Console.WriteLine($">>> 1 output token ~= the cost of {ratio:F0} input tokens <<<");

        if (suspect.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("WARNING — the coefficients are suspect, do not carry them into a report as-is:");
            foreach (var s in suspect) Console.WriteLine($"  - {s}");
        }

        Console.WriteLine();
        Console.WriteLine("Conclusion: prompt length is cheap, answer length is expensive.");
        Console.WriteLine("Be generous with top-k in RAG; 'answer briefly' is a performance decision.");

        Csv.Write(config, "02_prefill_decode",
            ["run_id", "model", "arm", "input_tokens", "output_tokens",
             "ttft_any_p50_ms", "total_p50_ms", "total_p95_ms",
             "temperature", "n_samples", "n_ttft_measured",
             "out_tok_min", "out_tok_max", "estimated_out_token_count", "max_tokens_truncated"],
            rows);

        Csv.Write(config, "02_coefficients",
            ["run_id", "model", "prefill_ms_per_in_token", "prefill_source",
             "prefill_ttft_ms_per_in_token", "prefill_ttft_r2",
             "prefill_total_ms_per_in_token", "prefill_total_r2",
             "decode_ms_per_out_token", "decode_r2", "tok_per_sec",
             "constant_overhead_ms", "b_arm_intercept_ms", "out_in_cost_ratio",
             "n_samples_per_point", "a_arm_temperature", "b_arm_temperature", "warnings"],
            [[runId, client.ModelId,
              Inv(a, "F5"), aFromTtft ? "ttft" : "total",
              Inv(aTtft, "F5"), Inv(aTtftR2, "F4"),
              Inv(aTotal, "F5"), Inv(aTotalR2, "F4"),
              Inv(b, "F4"), Inv(bR2, "F4"), Inv(b > 0 ? 1000 / b : 0, "F2"),
              Inv(pureOverhead, "F1"), Inv(bIntercept, "F1"),
              double.IsNaN(ratio) ? "" : Inv(ratio, "F1"),
              reps.ToString(), Inv(aTemp, "F2"), Inv(bTemp, "F2"),
              string.Join("; ", suspect)]]);
    }


    // ============================================================================
    // EXPERIMENT 3 — Determinism
    //
    // On Gemini you got 10/10 different outputs. Concluding "LLMs are
    // non-deterministic" from that would be WRONG. Model weights are a fixed
    // function; non-determinism comes from two places:
    //   1. sampling (temperature > 0)
    //   2. server-side batching / GPU floating-point summation order
    //
    // v1 bug: every output was the empty string; the "1/20 unique" result was the
    // equality of 20 empty strings. v2 also reads the reasoning channel and
    // reports empty output explicitly as an error.
    //
    // The temp=1.8 arm is a CONTROL arm: it verifies that the parameter reaches the
    // model. If there is no variety even there, temperature is not being applied.
    // ============================================================================
    static async Task Experiment3_Determinism(LmClient client, Config config, string runId)
    {
        Header("EXPERIMENT 3 — Determinism (4 arms)");

        const int n = 20;
        const string prompt =
            "List three elements that must be present in a progress payment report. Keep it short.";

        var arms = new (string Name, double Temp, int? Seed)[]
        {
            ("temp0_seed_fixed", 0.0, 42),
            ("temp0_seed_none",  0.0, null),
            ("temp08_seed_none", 0.8, null),
            ("temp18_seed_none", 1.8, null),   // control arm
        };

        var rows = new List<string[]>();
        Console.WriteLine($"{"arm",-20} {"unique",12} {"excl_empty",11} {"same_as_first",13} {"avg_chars",9} {"empty",5}");
        Console.WriteLine(new string('-', 74));

        foreach (var (name, temp, seed) in arms)
        {
            var results = new List<ChatResult>();
            for (var i = 0; i < n; i++)
            {
                results.Add(await client.ChatAsync([new Message("user", prompt)],
                    2500, temp, seed: seed, disableThinking: true));
            }

            var outputs = results.Select(r => r.Text.Trim()).ToList();
            var nonEmpty = outputs.Where(o => o.Length > 0).ToList();

            var emptyCount = outputs.Count(string.IsNullOrEmpty);
            var distinct = outputs.Distinct().Count();

            // Empty outputs were counted as "unique" too: one unit of the 20/20 in
            // the temp=1.8 arm was a single empty string. We report the count
            // excluding empties separately.
            var distinctNonEmpty = nonEmpty.Distinct().Count();
            var samePct = 100.0 * outputs.Count(o => o == outputs[0]) / n;
            var avgLen = outputs.Average(o => o.Length);
            var avgLenNonEmpty = nonEmpty.Count > 0 ? nonEmpty.Average(o => o.Length) : 0;

            // v1's bug was never reading reasoning at all; Experiment 3 still only
            // kept Text and threw away reasoning, the token counts and the timing —
            // so the reason for an empty output (did the budget go to reasoning?)
            // was invisible in the data.
            var avgReasoningChars = results.Average(r => (double)r.Reasoning.Length);
            var avgCompletionTok = results.Average(r => (double)r.CompletionTokens);
            var avgReasoningTok = results.Average(r => (double)r.ReasoningTokens);
            var truncated = results.Count(r => r.FinishReason == "length");

            Console.WriteLine($"{name,-20} {distinct + "/" + n,12} {distinctNonEmpty + "/" + nonEmpty.Count,11} " +
                              $"{samePct,12:F0}% {avgLen,9:F0} {emptyCount,5}");

            if (emptyCount > 0)
                Console.WriteLine($"  WARNING: {emptyCount}/{n} outputs were EMPTY " +
                                  $"(avg. reasoning {avgReasoningChars:F0} chars / {avgReasoningTok:F0} tokens — " +
                                  "the budget may have gone to thinking).");

            if (truncated > 0)
                Console.WriteLine($"  WARNING: {truncated}/{n} outputs were cut at the max_tokens ceiling — " +
                                  "the variety count is comparing truncated texts.");

            rows.Add([runId, client.ModelId, name,
                      Inv(temp, "F1"), seed?.ToString() ?? "",
                      n.ToString(), distinct.ToString(),
                      Inv(samePct, "F1"), Inv(avgLen, "F0"), emptyCount.ToString(),
                      distinctNonEmpty.ToString(), Inv(avgLenNonEmpty, "F0"),
                      Inv(avgReasoningChars, "F0"), Inv(avgCompletionTok, "F0"),
                      Inv(avgReasoningTok, "F0"), truncated.ToString()]);

            // 17 of the 20 outputs never reached disk; graded variety metrics such
            // as Levenshtein or self-BLEU could not be computed after the fact.
            // Write all of them.
            var dump = new StringBuilder();
            dump.AppendLine($"# {name} — temp={temp}, seed={(seed?.ToString() ?? "none")}, n={n} — {runId}");
            for (var i = 0; i < results.Count; i++)
            {
                dump.AppendLine();
                dump.AppendLine($"----- output {i + 1}/{n} " +
                                $"(content {outputs[i].Length} chars, reasoning {results[i].Reasoning.Length} chars, " +
                                $"finish={results[i].FinishReason}) -----");
                dump.AppendLine();
                dump.AppendLine(outputs[i].Length == 0 ? "(empty)" : outputs[i]);
            }

            WriteTextOutput(config, $"03_sample_{name}.txt", dump.ToString(), runId);
        }

        Console.WriteLine(new string('-', 74));
        Console.WriteLine("Expectation: temp=0 -> 1/20 unique (deterministic).");
        Console.WriteLine("             temp=1.8 -> high variety. IF NOT, temperature is not reaching");
        Console.WriteLine("             the model (LM Studio 'Default Parameters' may be overriding it).");
        Console.WriteLine();
        Console.WriteLine("LIMIT: the calls are sequential — only one request is in flight at a time.");
        Console.WriteLine("       Non-determinism caused by server-side batching CANNOT be measured");
        Console.WriteLine("       with this design.");
        Console.WriteLine("LIMIT: 'unique' is exact string equality and saturates at n. It cannot tell");
        Console.WriteLine("       temp=0.8 from 1.8, which makes the control arm a weak signal.");

        Csv.Write(config, "03_determinism",
            ["run_id", "model", "arm", "temperature", "seed", "n",
             "unique_outputs", "same_as_first_pct", "avg_chars", "empty_output_count",
             "unique_excl_empty", "avg_chars_excl_empty",
             "avg_reasoning_chars", "avg_completion_tokens", "avg_reasoning_tokens",
             "max_tokens_truncated"],
            rows);
    }


    // ============================================================================
    // EXPERIMENT 4 — Statelessness + KV cache (long context)
    //
    // There are two separate truths here, do not conflate them:
    //
    // (a) At the protocol level: every turn you resend the whole history.
    //     Token cost accumulates quadratically. That does not change.
    //
    // (b) At the server level: for a request continuing with the same prefix,
    //     prefill is NOT RECOMPUTED, it comes from the KV cache.
    //
    // v1 bug: the prompts were 42-162 tokens. With a prefill coefficient of ~0.19
    // ms, the most the cache could save is ~30 ms — invisible inside a 1400 ms
    // total. The experiment measured noise.
    //
    // v2: we put a long document into the system message. We are now in the
    // prefill-dominated regime and the cache effect produces a signal.
    //
    // Architectural consequence: fixed content (system prompt + contract text)
    // FIRST, variable content (the user's question) LAST. Breaking that order is a
    // performance bug.
    // ============================================================================
    static async Task Experiment4_KvCache(LmClient client, Config config, string runId)
    {
        Header($"EXPERIMENT 4 — KV cache (long context: ~{config.DocWords} words)");

        var document = Filler(config.DocWords);

        var turns = new[]
        {
            "According to this document, what is a progress payment? Explain briefly.",
            "What is the difference between an interim and a final progress payment?",
            "How does the price difference enter this calculation?",
            "What changes if there is a work increase?",
            "Summarize all of this in three bullet points.",
        };

        var rows = new List<string[]>();
        var systemBase = "Answer according to the contract document below.\n\n" + document;
        var emptyAssistantTurns = 0;

        foreach (var cacheFriendly in new[] { true, false })
        {
            var mode = cacheFriendly ? "cache_friendly" : "cache_hostile";
            Console.WriteLine($"\n{mode}:");
            Console.WriteLine($"{"turn",4} {"prompt_tok",12} {"cumulative",12} {"answer_chr",10} {"ttftAny",10} {"total",10}");
            Console.WriteLine(new string('-', 62));

            var history = new List<Message> { new("system", systemBase) };
            var cumulativePrompt = 0;
            var cumulativeTotal = 0;

            for (var t = 0; t < turns.Length; t++)
            {
                // Cache-hostile arm: on every turn we put a unique value at the
                // FRONT of the system message. Changing even a single character
                // breaks the prefix match and the whole prefill is recomputed. The
                // difference we measure is exactly that.
                history[0] = cacheFriendly
                    ? new Message("system", systemBase)
                    : new Message("system", $"[{Guid.NewGuid():N}]\n" + systemBase);

                history.Add(new Message("user", turns[t]));

                var r = await client.ChatStreamAsync(history, 150, 0, disableThinking: true);

                // If the model produced only reasoning, content comes back empty. We
                // do NOT write reasoning into the history (a model does not read its
                // own thoughts back), but silently writing "(empty)" was wrong too:
                // the CSV then read as though a 150-token answer had been appended.
                // We record the truth in a separate column.
                var answer = r.Text.Trim();
                if (answer.Length == 0) emptyAssistantTurns++;
                history.Add(new Message("assistant", answer.Length == 0 ? "(no answer)" : answer));

                cumulativePrompt += r.PromptTokens;
                cumulativeTotal += r.PromptTokens + r.CompletionTokens;

                Console.WriteLine($"{t + 1,4} {r.PromptTokens,12} {cumulativePrompt,12} {answer.Length,10} " +
                                  $"{Fmt(r.TtftAnyMs),10} {r.TotalMs,10:F1}");

                rows.Add([runId, client.ModelId, mode, (t + 1).ToString(),
                          r.PromptTokens.ToString(), cumulativePrompt.ToString(),
                          r.CompletionTokens.ToString(),
                          Inv(r.TtftAnyMs, "F2"), Inv(r.TotalMs, "F2"),
                          cumulativeTotal.ToString(), answer.Length.ToString(),
                          r.Reasoning.Length.ToString(), r.FinishReason]);
            }
        }

        // Turn 1 excluded: on the first turn both arms are cold, the comparison is
        // meaningless. The filter is on the turn column rather than a positional
        // Skip(1): if the loop order changed, Skip(1) would silently drop the wrong
        // row. An unmeasured ttft (-1) does not enter the average.
        static double[] Ttfts(List<string[]> src, string mode) => src
            .Where(r => r[2] == mode && r[3] != "1")
            .Select(r => double.Parse(r[7], CultureInfo.InvariantCulture))
            .Where(v => v >= 0)
            .ToArray();

        var friendlyVals = Ttfts(rows, "cache_friendly");
        var hostileVals = Ttfts(rows, "cache_hostile");

        if (friendlyVals.Length == 0 || hostileVals.Length == 0)
        {
            Console.WriteLine(new string('-', 62));
            Console.WriteLine("WARNING: not enough ttft measurements for the comparison, skipping the summary.");
        }
        else
        {
            // Median — that is this module's own rule (see the Stats block). Average
            // was used here, and a single thermal spike could distort the headline
            // number.
            var friendly = Stats.Median(friendlyVals);
            var hostile = Stats.Median(hostileVals);

            // The raw difference can mislead: the cache_hostile arm's prompt is a few
            // tokens longer because of the GUID. We also give the per-token normalized
            // figure.
            var friendlyTok = rows.Where(r => r[2] == "cache_friendly" && r[3] != "1")
                                  .Average(r => double.Parse(r[4], CultureInfo.InvariantCulture));
            var hostileTok = rows.Where(r => r[2] == "cache_hostile" && r[3] != "1")
                                 .Average(r => double.Parse(r[4], CultureInfo.InvariantCulture));

            Console.WriteLine(new string('-', 62));
            Console.WriteLine($"Median ttftAny (turns 2-5): cache_friendly {friendly:F0} ms | cache_hostile {hostile:F0} ms");
            Console.WriteLine($"Cache saving: {hostile - friendly:F0} ms  ({100 * (hostile - friendly) / Math.Max(hostile, 1):F0}%)");
            Console.WriteLine($"Prompt size (turns 2-5 avg.): friendly {friendlyTok:F0} tok | hostile {hostileTok:F0} tok");
            Console.WriteLine($"Prefill per token: friendly {friendly / Math.Max(friendlyTok, 1):F4} ms | " +
                              $"hostile {hostile / Math.Max(hostileTok, 1):F4} ms/token");
            Console.WriteLine();
            Console.WriteLine("prompt_tokens grows similarly in both arms (the protocol truth does not change),");
            Console.WriteLine("but ttft stays low in the cache_friendly arm (a server-side optimization).");
        }

        if (emptyAssistantTurns > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"WARNING: the assistant answer came back EMPTY on {emptyAssistantTurns} turns and was");
            Console.WriteLine("         appended to the history as '(no answer)'. So this is not a REAL five-turn");
            Console.WriteLine("         conversation; the completion_tokens column does not show that the answer");
            Console.WriteLine("         entered the history. The KV cache comparison is still valid, the");
            Console.WriteLine("         conversational narrative is not.");
        }

        Csv.Write(config, "04_kv_cache",
            ["run_id", "model", "mode", "turn", "prompt_tokens", "cumulative_prompt_tokens",
             "completion_tokens", "ttft_any_ms", "total_ms",
             "cumulative_total_tokens", "assistant_answer_chars", "reasoning_chars", "finish_reason"],
            rows);
    }


    // ============================================================================
    // EXPERIMENT 5 — Concurrency and throughput
    //
    // On Gemini the limit was the rate limit (burst 429 vs daily quota 429).
    // Locally there is no 429; the limit is the GPU itself. What we measure has
    // changed:
    //
    //   - aggregate throughput (tok/s): RISES up to a point, then saturates
    //   - per-request latency: degrades FROM THE VERY START
    //
    // Both are true at the same time. The classic throughput-latency trade-off.
    //
    // Why it matters: it answers "how many concurrent users can a single-GPU local
    // setup carry". Capacity planning comes out of this curve.
    // ============================================================================
    static async Task Experiment5_Concurrency(LmClient client, Config config, string runId)
    {
        Header("EXPERIMENT 5 — Concurrency / throughput");

        var levels = new[] { 1, 2, 4, 8, 16, 32,64 };
        var rows = new List<string[]>();

        Console.WriteLine($"{"parallel",8} {"ok",9} {"total tok/s",14} {"p50 lat",10} {"p95 lat",10} {"req/s",10} {"errors",6}");
        Console.WriteLine(new string('-', 72));

        foreach (var level in levels)
        {
            var errors = new List<string>();
            var errorLock = new object();
            var sw = Stopwatch.StartNew();

            var tasks = Enumerable.Range(0, level).Select(async _ =>
            {
                // Unique prefix: we stop parallel requests from benefiting from each
                // other's cache. Otherwise we would measure a fake throughput rise at
                // high concurrency.
                var prompt = $"[{Guid.NewGuid():N}] Explain construction site occupational safety measures.";
                try
                {
                    return await client.ChatStreamAsync(
                        [new Message("user", prompt)], 200, 0.7, disableThinking: true);
                }
                catch (Exception ex)
                {
                    // Let the error KIND make it into the persisted data: without
                    // distinguishing 429 / timeout / dropped connection / 500 there
                    // was no way to tell afterwards whether the limit was on the
                    // client or the server.
                    lock (errorLock) errors.Add(ex.GetType().Name);
                    Console.WriteLine($"  error ({ex.GetType().Name}): {Trunc(ex.Message, 120)}");
                    return null;
                }
            }).ToArray();

            var all = await Task.WhenAll(tasks);
            sw.Stop();

            var ok = all.Where(r => r is not null).Select(r => r!).ToArray();
            var failed = all.Length - ok.Length;
            var wallSec = sw.Elapsed.TotalSeconds;
            var errorKinds = string.Join("|", errors.GroupBy(e => e).Select(g => $"{g.Key}x{g.Count()}"));

            if (ok.Length == 0)
            {
                // The row IS written. It used to `continue`, and the CSV could not
                // distinguish "collapsed completely" from "never attempted".
                Console.WriteLine($"{level,8} {0,9} {"-",14} {"-",10} {"-",10} {"-",10} {failed,6}");

                rows.Add([runId, client.ModelId, level.ToString(), "0",
                          Inv(wallSec, "F3"), "", "", "", "", failed.ToString(),
                          "0", errorKinds, "1"]);

                await Task.Delay(3000);
                continue;
            }

            var totalOut = ok.Sum(r => r.CompletionTokens);
            var aggTps = totalOut / wallSec;
            var p50 = Stats.Median(ok.Select(r => r.TotalMs));
            var p95 = Stats.Percentile(ok.Select(r => r.TotalMs), 95);
            var rps = ok.Length / wallSec;

            Console.WriteLine($"{level,8} {ok.Length,9} {aggTps,14:F1} {p50,10:F0} {p95,10:F0} {rps,10:F2} {failed,6}");

            if (failed > 0)
                Console.WriteLine($"  WARNING: the wall clock covers ALL requests (failures included), " +
                                  $"so throughput looks low because of {failed} errors.");

            rows.Add([runId, client.ModelId, level.ToString(), totalOut.ToString(),
                      Inv(wallSec, "F3"), Inv(aggTps, "F2"),
                      Inv(p50, "F1"), Inv(p95, "F1"), Inv(rps, "F3"), failed.ToString(),
                      ok.Length.ToString(), errorKinds, failed > 0 ? "1" : "0"]);

            // Pause so the GPU can recover — thermal and memory pressure must not
            // leak into the next level. The measurement environment is data too.
            await Task.Delay(3000);
        }

        Console.WriteLine(new string('-', 72));
        Console.WriteLine("Saturation point: the concurrency level at which total tok/s stops rising.");
        Console.WriteLine("The capacity decision is taken BEFORE that point, against your p95 latency budget.");
        Console.WriteLine();
        Console.WriteLine("LIMIT: p95 here is computed by nearest-rank and is mathematically EQUAL to the");
        Console.WriteLine("       MAXIMUM while n <= 20 (n = number of successful requests). So p50/p95 is");
        Console.WriteLine("       not a tail statistic but the slowest request of a single batch.");
        Console.WriteLine("LIMIT: the levels are run once and always in increasing order — the highest");
        Console.WriteLine("       concurrency is always measured on the hottest GPU. No repetition or");
        Console.WriteLine("       counterbalancing.");
        Console.WriteLine("VERIFY FIRST: the server's Max Concurrent Predictions setting must be GREATER");
        Console.WriteLine("       than the highest level tested; otherwise what you measure is not GPU");
        Console.WriteLine("       saturation but a config queue, and the 'saturation point' is circular.");

        Csv.Write(config, "05_concurrency",
            ["run_id", "model", "concurrency", "total_output_tokens", "wall_clock_sec",
             "total_tok_per_sec", "p50_latency_ms", "p95_latency_ms", "req_per_sec", "error_count",
             "successful_requests", "error_kinds", "unreliable"],
            rows);
    }


    // ============================================================================
    // EXPERIMENT 6 — Quality spot check
    //
    // Local inference is not free; you pay in quality. This experiment does NOT
    // produce an automatic SCORE — deliberately. You will build a real eval harness
    // in phases 3-4; the goal here is to dump the outputs to disk and read them by
    // hand.
    //
    // v2: two runs, thinking ON and OFF, so that reasoning's contribution to
    // quality and its cost (reasoning tokens are counted on the output side, i.e.
    // the expensive side) sit side by side.
    // ============================================================================
    static async Task Experiment6_QualitySpotCheck(LmClient client, Config config, string runId)
    {
        Header("EXPERIMENT 6 — Quality spot check (to be assessed by hand)");

        var prompts = new[]
        {
            "What is the difference between a progress payment report and a final account?",
            "Which base index is used in the price difference calculation, and why is it needed?",
            "Up to what percentage of the contract price can a work increase be made?",
            "What period runs between provisional acceptance and final acceptance?",
            "How is the guarantee for an advance paid to the contractor released?",
            "Which annexes are found in a progress payment file? Write them as a list.",
            "Extract the contract price and the date from the following text as JSON: " +
            "\"Under the contract dated 08.03.2026, the work was undertaken for a price of 12,450,000.00 TRY.\" " +
            "Return only JSON, no explanation.",
            "What do you do if you are asked about a regulation article you do not know? Answer briefly.",
        };

        var sb = new StringBuilder();
        sb.AppendLine($"# Quality spot check — {client.ModelId} — {runId}");
        sb.AppendLine();
        sb.AppendLine("Assess each answer by hand: [C]orrect / [P]artial / [W]rong / [H]allucination");
        sb.AppendLine();
        sb.AppendLine("Note: this model was not trained on the regulations. The goal is to see the");
        sb.AppendLine("baseline BEFORE RAG — errors are the expected result and are the evidence for");
        sb.AppendLine("why RAG is needed.");
        sb.AppendLine();
        sb.AppendLine("Each question is asked twice: with thinking ON and OFF. Reasoning tokens are");
        sb.AppendLine("counted on the output side (the expensive side), so whether the quality gain");
        sb.AppendLine("is worth the cost is a measurable question.");
        sb.AppendLine();

        // If thinking really is not being disabled, the two arms send the SAME
        // request and the experiment measures nothing. We say so up front and stamp
        // it into the output — in older runs the two arms produced byte-identical
        // token counts and the problem was only visible when reading the file by hand.
        if (!client.CanDisableThinking)
        {
            Console.WriteLine("WARNING: thinking cannot be disabled (pre-flight check). A single arm will run;");
            Console.WriteLine("         the 'on' / 'off' comparison will not be made in this run.");
            sb.AppendLine("> **WARNING:** thinking could not be disabled in the pre-flight check. The");
            sb.AppendLine("> outputs below belong to a single arm; the on/off quality-cost comparison");
            sb.AppendLine("> HAS NOT BEEN MADE. Both arms would send the identical request, so identical");
            sb.AppendLine("> token counts are the expected result and are NOT a quality/cost comparison.");
            sb.AppendLine();
        }

        var rows = new List<string[]>();

        for (var i = 0; i < prompts.Length; i++)
        {
            Console.WriteLine($"  [{i + 1}/{prompts.Length}] {Trunc(prompts[i], 50)}");

            sb.AppendLine($"## Question {i + 1}");
            sb.AppendLine($"**Prompt:** {prompts[i]}");
            sb.AppendLine();

            var arms = client.CanDisableThinking ? new[] { true, false } : new[] { true };

            foreach (var think in arms)
            {
                var label = think ? "thinking ON" : "thinking OFF";
                var r = await client.ChatAsync([new Message("user", prompts[i])],
                    3000, 0, seed: 42, disableThinking: !think);

                // max_tokens covers the SUM of reasoning + content. When the budget
                // goes to thinking and content never gets a turn, that is not an
                // "error" but a BUDGET event; putting both under the same label
                // produced a wrong diagnosis.
                var truncated = r.FinishReason == "length";
                var body = r.Text.Trim();

                sb.AppendLine($"### {label}");
                sb.AppendLine();

                if (body.Length > 0)
                {
                    sb.AppendLine(body);
                    if (truncated)
                        sb.AppendLine("\n_(cut at the max_tokens ceiling — the answer is incomplete)_");
                }
                else if (truncated)
                {
                    sb.AppendLine($"_(NO visible answer — the entire {r.ReasoningTokens}-token budget went to " +
                                  "thinking and hit the max_tokens ceiling; this is not an error, it is a " +
                                  "budget event)_");
                }
                else
                {
                    sb.AppendLine("_(empty output — error)_");
                }

                sb.AppendLine();
                sb.AppendLine($"*{r.PromptTokens} in / {r.CompletionTokens} out " +
                              $"(reasoning: {r.ReasoningTokens}) — {r.TotalMs:F0} ms — finish: {r.FinishReason}*");
                sb.AppendLine();

                // The reasoning text was never written to disk; a claim like "it
                // thought for 1016 tokens and still answered wrongly" could not be
                // verified from the output files.
                if (r.Reasoning.Length > 0)
                {
                    sb.AppendLine("<details><summary>reasoning (" + r.Reasoning.Length + " characters)</summary>");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(r.Reasoning.Trim());
                    sb.AppendLine("```");
                    sb.AppendLine();
                    sb.AppendLine("</details>");
                    sb.AppendLine();
                }

                sb.AppendLine("**Assessment:** _(C/P/W/H — fill in by hand)_");
                sb.AppendLine();

                rows.Add([runId, client.ModelId, (i + 1).ToString(), think ? "on" : "off",
                          r.PromptTokens.ToString(), r.CompletionTokens.ToString(),
                          r.ReasoningTokens.ToString(), Inv(r.TotalMs, "F0"),
                          r.Text.Length.ToString(), "",
                          r.Reasoning.Length.ToString(), r.FinishReason,
                          truncated ? "1" : "0",
                          client.CanDisableThinking ? "1" : "0"]);
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        // We do NOT overwrite hand-filled assessments. It used to call
        // File.WriteAllText on the same name, and a second run silently destroyed
        // hours of human effort.
        var path = WriteTextOutput(config, "06_quality_spot_check.md", sb.ToString(), runId);

        Csv.Write(config, "06_quality_metrics",
            ["run_id", "model", "question_no", "thinking", "prompt_tokens", "completion_tokens",
             "reasoning_tokens", "total_ms", "answer_chars", "assessment",
             "reasoning_chars", "finish_reason", "max_tokens_truncated", "thinking_could_be_disabled"],
            rows);

        Console.WriteLine($"\nOutput: {path}");
        Console.WriteLine("Fill this file in by hand. It will be the seed of the phase 3 eval data set.");

        if (client.CanDisableThinking)
        {
            var identical = rows.Where((_, idx) => idx % 2 == 0)
                                .Zip(rows.Where((_, idx) => idx % 2 == 1),
                                     (on, off) => on[5] == off[5] && on[6] == off[6] && on[8] == off[8])
                                .Count(same => same);

            if (identical > 0)
                Console.WriteLine($"WARNING: on {identical}/{prompts.Length} questions the two arms produced " +
                                  "byte-identical results — thinking is believed to be off but is not.");
        }
        else
        {
            Console.WriteLine("NOTE: only one arm was run because thinking could not be disabled; " +
                              "the on/off comparison WAS NOT MADE in this run.");
        }
    }


    // ============================================================================
    // Helpers
    // ============================================================================

    static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 62));
        Console.WriteLine(title);
        Console.WriteLine(new string('=', 62));
    }

    static string Inv(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

    static string Fmt(double ttft) => ttft < 0 ? "n/a" : $"{ttft:F0}ms";

    static string Trunc(string s, int max) => Text.Trunc(s, max);

    /// <summary>
    /// Writes a text output but DOES NOT OVERWRITE an existing one. Experiment 3's
    /// samples and Experiment 6's Markdown contain hand-filled fields; calling
    /// File.WriteAllText on a fixed name silently destroyed human effort on the
    /// second run. If the file exists we write to a run_id-suffixed sibling and
    /// return that path.
    /// </summary>
    static string WriteTextOutput(Config config, string fileName, string content, string runId)
    {
        var path = Path.Combine(config.OutputDir, fileName);

        if (File.Exists(path))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            path = Path.Combine(config.OutputDir, $"{stem}.{runId}{ext}");
            Console.WriteLine($"   NOTE: {fileName} already exists (it may have been filled in by hand), " +
                              $"the new output was written as {Path.GetFileName(path)}.");
        }

        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    // Filler text of a given length.
    //
    // NOTE: this vocabulary used to be Turkish on purpose, so that the prefill
    // measurement reflected the real usage scenario (including the token penalty
    // from Experiment 1). It is English now, which makes the prefill/KV-cache
    // measurements cheaper per word than the Turkish workload they stand in for.
    static string Filler(int words)
    {
        var vocab = new[]
        {
            "contractor", "administration", "contract", "progress payment", "production",
            "quantity", "estimate", "unit", "price", "guarantee", "site", "tender",
            "technical", "specification", "acceptance", "inspection", "minutes",
            "payment", "delay", "penalty", "clause", "index", "advance", "revision",
            "increase", "decrease", "audit", "report",
        };

        var sb = new StringBuilder();
        for (var i = 0; i < words; i++)
        {
            sb.Append(vocab[i % vocab.Length]);
            sb.Append(i % 12 == 11 ? ". " : " ");
        }
        return sb.ToString();
    }


    // ============================================================================
    // LM Studio client (OpenAI-compatible /v1)
    // ============================================================================

    sealed class LmClient(HttpClient http, string baseUrl, string? modelId)
    {
        public string ModelId { get; set; } = modelId ?? "";

        /// <summary>
        /// The /no_think system-message fallback for when enable_thinking is not
        /// supported. Set at run time based on the pre-flight check result.
        /// </summary>
        public bool UseNoThinkFallback { get; set; }

        /// <summary>
        /// Did the pre-flight check confirm that thinking can genuinely be disabled?
        /// Experiment 6 reads this to emit its "both arms send the same request"
        /// warning.
        /// </summary>
        public bool CanDisableThinking { get; set; }

        static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public async Task<List<string>> ListModelsAsync()
        {
            using var res = await http.GetAsync($"{baseUrl}/models");
            res.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return [];

            return data.EnumerateArray()
                .Select(e => e.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .ToList();
        }

        // ------------------------------------------------------------------------
        // Non-streaming
        // ------------------------------------------------------------------------

        public async Task<ChatResult> ChatAsync(
            List<Message> messages, int maxTokens, double temperature,
            int? seed = null, bool disableThinking = false)
        {
            var body = BuildBody(messages, maxTokens, temperature, seed, stream: false, disableThinking);
            var sw = Stopwatch.StartNew();

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json")
            };

            using var res = await http.SendAsync(req);
            var raw = await res.Content.ReadAsStringAsync();
            sw.Stop();

            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"{(int)res.StatusCode} — {Text.Trunc(raw, 400)}");

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // Some OpenAI-compatible servers return an error with HTTP 200 and an
            // { "error": ... } body. The stream path had this check, this one did
            // not: the result passed silently as an empty ChatResult and turned into
            // innocent-looking but wrong numbers such as "ratio 0.000" in Experiment 1.
            if (root.TryGetProperty("error", out var errEl))
                throw new InvalidOperationException($"server error (HTTP 200) — {Text.Trunc(errEl.ToString(), 400)}");

            var text = "";
            var reasoning = "";
            var finishReason = "";

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                if (choices[0].TryGetProperty("message", out var msg))
                {
                    // Reasoning models use a separate field here too.
                    // v1 only looked at content and got the empty string.
                    text = ReadStringProp(msg, "content") ?? "";
                    reasoning = ReadStringProp(msg, "reasoning_content")
                             ?? ReadStringProp(msg, "reasoning")
                             ?? "";
                }

                // finish_reason was never read: an answer cut at the max_tokens
                // ceiling could not be told apart from one that ended normally, and
                // truncation was labelled an "error".
                finishReason = ReadStringProp(choices[0], "finish_reason") ?? "";
            }

            var (pt, ct, rt) = ReadUsage(root);

            return new ChatResult(text, reasoning, pt, ct, rt, sw.Elapsed.TotalMilliseconds, finishReason);
        }

        // ------------------------------------------------------------------------
        // Streaming
        //
        // This is the only way to measure TTFT — in a non-stream call prefill and
        // decode are buried in a single duration and cannot be separated.
        //
        // Reasoning models have two distinct TTFTs and both are meaningful:
        //   TtftAnyMs     : the first token OF ANY KIND. The moment physical prefill
        //                   finished. This is what the prefill coefficient uses.
        //   TtftContentMs : the first token VISIBLE to the user. Perceived latency.
        //                   Timeout budgets and UI decisions are made from this one.
        //
        // Without the distinction, the model thinks "silently" for seconds and you
        // cancel a healthy request believing it timed out.
        // ------------------------------------------------------------------------

        public async Task<StreamResult> ChatStreamAsync(
            List<Message> messages, int maxTokens, double temperature,
            int? seed = null, bool disableThinking = false)
        {
            var body = BuildBody(messages, maxTokens, temperature, seed, stream: true, disableThinking);
            body["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json")
            };

            var sw = Stopwatch.StartNew();

            double ttftAny = -1;
            double ttftContent = -1;
            var contentSb = new StringBuilder();
            var reasoningSb = new StringBuilder();
            var pt = 0;
            var ct = 0;
            var rt = 0;

            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"{(int)res.StatusCode} — {Text.Trunc(await res.Content.ReadAsStringAsync(), 400)}");

            using var stream = await res.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var finishReason = "";
            var dataLines = 0;

            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;                  // SSE separator
                if (!line.StartsWith("data:")) continue;         // the space after ':' is OPTIONAL in SSE

                // It used to look for the fixed prefix "data: " (with a space) and
                // slice with line[6..]; against a server emitting "data:{...}" ALL
                // chunks were dropped silently, ttft could not be measured and v1's
                // ttft==total bug came back without any warning.
                var payload = line[5..].TrimStart();
                if (payload.Length == 0) continue;
                if (payload.AsSpan().Trim().SequenceEqual("[DONE]")) break;

                dataLines++;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(payload); }
                catch (JsonException) { continue; }              // a malformed chunk must not kill the run

                using (doc)
                {
                    var root = doc.RootElement;

                    if (root.TryGetProperty("error", out var err))
                        throw new InvalidOperationException($"stream error — {err}");

                    if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        if (choices[0].TryGetProperty("delta", out var delta)
                            && delta.ValueKind == JsonValueKind.Object)
                        {
                            // --- reasoning channel ---
                            // Different servers use different names; scan for both.
                            var reasoningChunk = ReadStringProp(delta, "reasoning_content")
                                              ?? ReadStringProp(delta, "reasoning");

                            if (!string.IsNullOrEmpty(reasoningChunk))
                            {
                                if (ttftAny < 0) ttftAny = sw.Elapsed.TotalMilliseconds;
                                reasoningSb.Append(reasoningChunk);
                            }

                            // --- visible content channel ---
                            var contentChunk = ReadStringProp(delta, "content");
                            if (!string.IsNullOrEmpty(contentChunk))
                            {
                                if (ttftAny < 0) ttftAny = sw.Elapsed.TotalMilliseconds;
                                if (ttftContent < 0) ttftContent = sw.Elapsed.TotalMilliseconds;
                                contentSb.Append(contentChunk);
                            }
                        }

                        var fr = ReadStringProp(choices[0], "finish_reason");
                        if (!string.IsNullOrEmpty(fr)) finishReason = fr;
                    }

                    // usage usually arrives in the last chunk; we read it on every
                    // chunk and overwrite.
                    var (p, c, r) = ReadUsage(root);
                    if (p > 0) pt = p;
                    if (c > 0) ct = c;
                    if (r > 0) rt = r;
                }
            }

            sw.Stop();
            var totalMs = sw.Elapsed.TotalMilliseconds;

            // ttftAny = -1 IS PRESERVED. It used to be folded down to totalMs, which
            // reproduced — unflagged — exactly the ttft == total condition described
            // at the top of this file as v1's main bug, and it entered the regression
            // as if it were a real measurement.
            // ttftContent = -1 is left alone for the same reason: it means no visible
            // content was ever produced. Writing 0 reads as "it arrived instantly"
            // and silently makes the CSV lie. Record missing data as missing.

            if (dataLines == 0)
                throw new InvalidOperationException(
                    "not a single 'data:' line could be read from the stream — the SSE format differs from what was expected.");

            // If the server did not send usage we ESTIMATE the token count, but we
            // flag it so estimate and measurement stay distinguishable in the CSV.
            var estimated = ct == 0;
            if (estimated)
                ct = Math.Max(1, (contentSb.Length + reasoningSb.Length) / 3);

            return new StreamResult(
                contentSb.ToString(), reasoningSb.ToString(),
                pt, ct, rt, ttftAny, ttftContent, totalMs, finishReason, estimated);
        }

        // ------------------------------------------------------------------------

        Dictionary<string, object> BuildBody(
            List<Message> messages, int maxTokens, double temperature,
            int? seed, bool stream, bool disableThinking)
        {
            var effective = messages;

            // Fallback: if chat_template_kwargs is not supported, the /no_think
            // system message also turns thinking off on Qwen.
            if (disableThinking && UseNoThinkFallback)
            {
                effective = [.. messages];
                if (effective.Count > 0 && effective[0].Role == "system")
                    effective[0] = new Message("system", effective[0].Content + "\n/no_think");
                else
                    effective.Insert(0, new Message("system", "/no_think"));
            }

            var body = new Dictionary<string, object>
            {
                ["model"] = ModelId,
                ["messages"] = effective.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                ["max_tokens"] = maxTokens,
                ["temperature"] = temperature,
                ["stream"] = stream,
            };

            if (seed.HasValue) body["seed"] = seed.Value;

            // Turn thinking off during measurement runs: we are measuring the
            // physics, not the reasoning.
            if (disableThinking)
                body["chat_template_kwargs"] = new Dictionary<string, object> { ["enable_thinking"] = false };

            return body;
        }

        static string? ReadStringProp(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        static (int Prompt, int Completion, int Reasoning) ReadUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
                return (0, 0, 0);

            var p = ReadInt(u, "prompt_tokens");
            var c = ReadInt(u, "completion_tokens");

            var r = 0;
            if (u.TryGetProperty("completion_tokens_details", out var d) && d.ValueKind == JsonValueKind.Object)
                r = ReadInt(d, "reasoning_tokens");

            return (p, c, r);

            // GetInt32 was unguarded: if a usage field arrived as a decimal such as
            // "128.0", or above int.MaxValue, it threw FormatException, and that
            // exception was not caught by the JsonException catch below.
            static int ReadInt(JsonElement el, string name)
            {
                if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
                    return 0;

                if (v.TryGetInt32(out var i)) return i;
                if (v.TryGetDouble(out var d) && d >= 0 && d <= int.MaxValue) return (int)Math.Round(d);

                return 0;
            }
        }
    }


    // ============================================================================
    // Record types
    // ============================================================================

    record Message(string Role, string Content);

    record ChatResult(
        string Text,
        string Reasoning,
        int PromptTokens,
        int CompletionTokens,
        int ReasoningTokens,
        double TotalMs,
        string FinishReason);   // "stop" | "length" | ... ; "" = the server did not report it

    record StreamResult(
        string Text,
        string Reasoning,
        int PromptTokens,
        int CompletionTokens,
        int ReasoningTokens,
        double TtftAnyMs,       // -1 = no token ever arrived, could not be measured
        double TtftContentMs,   // -1 = no visible content was ever produced
        double TotalMs,
        string FinishReason,
        bool CompletionTokensEstimated);   // true = the server sent no usage, this is an ESTIMATE


    // ============================================================================
    // Statistics
    //
    // We use the median, not the mean: on a GPU, Windows desktop composition or
    // thermal throttling produces occasional spikes that distort the mean. We also
    // report p95 separately because tail latency is the real input to the capacity
    // decision.
    // ============================================================================

    static class Stats
    {
        public static double Median(IEnumerable<double> values)
        {
            var s = values.OrderBy(v => v).ToArray();
            if (s.Length == 0) return 0;
            return s.Length % 2 == 1
                ? s[s.Length / 2]
                : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2.0;
        }

        public static double Percentile(IEnumerable<double> values, double p)
        {
            var s = values.OrderBy(v => v).ToArray();
            if (s.Length == 0) return 0;
            var idx = (int)Math.Ceiling(p / 100.0 * s.Length) - 1;
            return s[Math.Clamp(idx, 0, s.Length - 1)];
        }

        /// <summary>Least-squares linear fit. Returns: (slope, intercept).</summary>
        public static (double Slope, double Intercept) LinearFit(IEnumerable<double> xs, IEnumerable<double> ys)
        {
            var x = xs.ToArray();
            var y = ys.ToArray();
            if (x.Length < 2 || x.Length != y.Length) return (0, 0);

            var mx = x.Average();
            var my = y.Average();
            var num = x.Zip(y, (a, b) => (a - mx) * (b - my)).Sum();
            var den = x.Sum(a => (a - mx) * (a - mx));

            var slope = Math.Abs(den) < 1e-9 ? 0 : num / den;
            return (slope, my - slope * mx);
        }

        /// <summary>
        /// Coefficient of determination. LinearFit alone did not say whether the fit
        /// was GOOD: when den ~ 0 it silently returned a slope of 0, and that value
        /// was reported as if it were a real measurement. Print the coefficient
        /// together with R².
        /// </summary>
        public static double RSquared(IEnumerable<double> xs, IEnumerable<double> ys, double slope, double intercept)
        {
            var x = xs.ToArray();
            var y = ys.ToArray();
            if (x.Length < 2 || x.Length != y.Length) return double.NaN;

            var my = y.Average();
            var ssTot = y.Sum(v => (v - my) * (v - my));
            if (ssTot < 1e-12) return double.NaN;

            var ssRes = x.Zip(y, (a, b) => b - (slope * a + intercept)).Sum(e => e * e);
            return 1 - ssRes / ssTot;
        }
    }


    // ============================================================================
    // Text helpers
    // ============================================================================

    static class Text
    {
        public static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";

            // Cutting on a UTF-16 code unit can split a surrogate pair; we pull the
            // boundary back by one unit so we never produce half a character.
            var cut = max;
            if (char.IsHighSurrogate(s[cut - 1])) cut--;

            return s[..cut] + "...";
        }
    }


    // ============================================================================
    // CSV
    // ============================================================================

    static class Csv
    {
        /// <summary>
        /// If a file of the same name EXISTS it appends rows rather than
        /// overwriting. Because every row carries a run_id, runs genuinely become
        /// comparable — previously File.WriteAllText meant each run silently deleted
        /// the previous one, while the code still claimed "runs are comparable".
        ///
        /// If the header changed (the schema was updated), appending to the old file
        /// would corrupt the data; in that case the old one is kept as .bak and a new
        /// file is started.
        /// </summary>
        public static void Write(Config config, string name, string[] header, List<string[]> rows)
        {
            var path = Path.Combine(config.OutputDir, $"{name}.csv");
            var headerLine = string.Join(",", header.Select(Escape));
            var append = false;

            if (File.Exists(path))
            {
                var existingHeader = File.ReadLines(path, new UTF8Encoding(true)).FirstOrDefault()?.TrimStart('﻿');

                if (existingHeader == headerLine)
                {
                    append = true;
                }
                else
                {
                    var backup = path + ".bak";
                    File.Move(path, backup, overwrite: true);
                    Console.WriteLine($"   NOTE: the schema of {name}.csv changed, the old one was kept as {name}.csv.bak.");
                }
            }

            var sb = new StringBuilder();
            if (!append) sb.AppendLine(headerLine);
            foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Escape)));

            // BOM: so that Excel reads non-ASCII characters correctly.
            if (append)
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(true));
            else
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));

            Console.WriteLine($"-> {path}{(append ? $" (+{rows.Count} rows appended)" : "")}");
        }

        static string Escape(string v)
        {
            v ??= "";

            // '\r' must be quoted too: a lone CR corrupted the line structure in a
            // BOM-prefixed (Excel-targeted) file.
            var needsQuote = v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r');

            // Formula injection: Excel treats cells starting with '=', '+', '-' or
            // '@' as formulas. ModelId and the hand-filled columns come from the
            // server or from a human, so we neutralize them by quoting and prefixing
            // with an apostrophe.
            if (v.Length > 0 && (v[0] is '=' or '+' or '@'))
                return "\"'" + v.Replace("\"", "\"\"") + "\"";

            return needsQuote ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
    }


    // ============================================================================
    // Configuration
    // ============================================================================

    sealed class Config
    {
        public string BaseUrl { get; init; } = "http://localhost:1234/v1";
        public string? ModelId { get; init; }
        public string OutputDir { get; init; } = "results";
        public int DocWords { get; init; } = 2000;
        public HashSet<int> Experiments { get; init; } = [1, 2, 3, 4, 5, 6];

        public const string Usage =
            "Usage: dotnet run -- 1 [--url <address>] [--model <id>] [--out <directory>] " +
            "[--doc-words <count>] [1..6 ...]";

        /// <summary>
        /// Argument parsing. Anything unrecognized is now an ERROR: a typo such as
        /// '--modle qwen' used to be swallowed silently and fall through to
        /// auto-discovery, while 'dotnet run -- 7' selected no experiment at all and
        /// therefore ran all of them.
        /// </summary>
        public static Config Parse(string[] args)
        {
            var url = "http://localhost:1234/v1";
            string? model = null;
            var outDir = "results";
            var docWords = 2000;
            var exps = new HashSet<int>();
            var errors = new List<string>();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                switch (arg)
                {
                    case "--url":
                    case "--model":
                    case "--out":
                    case "--doc-words":
                        if (i + 1 >= args.Length)
                        {
                            errors.Add($"{arg} expects a value but the argument list ended");
                            break;
                        }

                        var value = args[++i];

                        if (arg == "--url") url = value;
                        else if (arg == "--model") model = value;
                        else if (arg == "--out") outDir = value;
                        else if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out docWords) || docWords < 1)
                            errors.Add($"--doc-words must be a positive integer, got '{value}'");

                        break;

                    default:
                        if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        {
                            if (n is >= 1 and <= 6) exps.Add(n);
                            else errors.Add($"the experiment number must be in the range 1-6, got '{n}'");
                        }
                        else
                        {
                            errors.Add($"unrecognized argument: '{arg}'");
                        }

                        break;
                }
            }

            if (errors.Count > 0)
                throw new ArgumentException(string.Join("\n  - ", errors.Prepend("Argument error:")));

            return new Config
            {
                BaseUrl = url.TrimEnd('/'),
                ModelId = model,
                OutputDir = outDir,
                DocWords = docWords,
                Experiments = exps.Count > 0 ? exps : [1, 2, 3, 4, 5, 6],
            };
        }
    }
}
