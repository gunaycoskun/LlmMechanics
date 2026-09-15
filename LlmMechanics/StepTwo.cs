// ============================================================================
// Phase 2 — Structured Output Harness (Local / LM Studio)
//
// Experiment 1: does schema enforcement collapse the variance measured in
// Phase 1, what does it cost, and WHICH schema is being enforced?
//
// Five modes. The schema dimension is what v2 adds — the previous run compared
// "no schema / described / enforced" against a single BADLY DESIGNED contract
// and concluded that enforcement makes the model lie. That conclusion was one
// word short: a badly designed enforced schema makes the model lie.
//
//   A_prose            : "return JSON", nothing else
//   B_strict_described : the strict contract, described in the prompt as text
//   C_strict_enforced  : the strict contract, via response_format (grammar)
//   B_escape_described : the escape-hatch contract, described as text
//   C_escape_enforced  : the escape-hatch contract, via response_format
//
// strict vs escape — the only difference is whether the contract lets the model
// say "the text does not support an answer here":
//   strict : para_birimi enum is TRY|USD|EUR (the truth, CHF, is outside it);
//            tarih and teminat_orani are required and NOT nullable
//   escape : the enum also has CHF and DIGER; tarih and teminat_orani are
//            nullable; and there is a bulunamayan_alanlar array the model can
//            list unanswerable fields in
//
// The measured effect of the strict contract (run 5, hard task):
//   para_birimi   eur x18/18   — the truth is CHF, the grammar cannot emit it
//   teminat_orani   0 x18/18   — absent from the text, null not permitted
//   tarih   2024-03-15 x18/18  — no date in the text at all
// 100% consistency at 0% accuracy: twenty runs agreeing on the same wrong
// answer. Mode B, given the same information but no grammar, split 9/8 between
// 0 and null on teminat_orani — less consistent and more honest. A schema does
// not remove uncertainty; a schema with no escape hatch HIDES it.
//
// Two tasks (--task):
//   easy : every field stated plainly. Hit a ceiling in runs 2-4 (B and C both
//          100% on everything), so it cannot separate description from grammar.
//   hard : the same facts written the way real documents are — the amount in
//          words, a vague date with no year, a currency outside the strict enum,
//          and a required field the text never mentions.
//
// What Phase 1 taught us and is carried over here:
//   - VERIFY, do not assume. The pre-flight check confirms response_format is
//     honoured AND that the enum actually binds, before anything is measured.
//   - Reasoning eats the budget. The hard task averaged ~6800 reasoning tokens
//     against a max_tokens of 8000, and 5 of 60 runs were cut off. Truncation is
//     reported per mode and the default budget is now 12000.
//   - Record missing data as missing. No unmeasured value is rounded to
//     something plausible.
//
// No dependencies. Raw HttpClient, System.Text.Json for parsing.
//
// Running it (the first argument, 2, selects this step — see Program.cs):
//   dotnet run -- 2 --model qwen/qwen3.5-9b                    (hard, both schemas)
//   dotnet run -- 2 --model qwen/qwen3.5-9b --schema escape    (escape only)
//   dotnet run -- 2 --model qwen/qwen3.5-9b --task easy --n 10
// ============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmMechanics;

static class StepTwo
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        Config config;
        try { config = Config.Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Config.Usage);
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var client = new LmClient(http, config.BaseUrl, config.ModelId);

        Directory.CreateDirectory(config.OutputDir);

        Console.WriteLine($"Base URL : {config.BaseUrl}");
        Console.WriteLine($"Model    : {config.ModelId}");
        Console.WriteLine($"n        : {config.N} per mode");
        Console.WriteLine($"temp     : {config.Temperature}");
        Console.WriteLine($"task     : {config.Task}");
        Console.WriteLine($"schema   : {config.Schema}");
        Console.WriteLine($"max tok  : {config.MaxTokens}");

        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Console.WriteLine($"Run ID   : {runId}");

        var task = config.Task == "hard" ? Tasks.Hard : Tasks.Easy;
        var fields = task.Fields;
        var groundTruth = task.GroundTruth;

        // ----------------------------------------------------------------------------
        // Warm-up — excluded from measurement (Phase 1: the first call absorbs model
        // loading, CUDA init and kernel compilation; measured gap 2413 ms vs 65 ms).
        // ----------------------------------------------------------------------------

        Console.WriteLine("\nWarm-up (3 calls, excluded)...");
        try
        {
            for (var i = 0; i < 3; i++)
                await client.ChatAsync([new Message("user", "Hello.")], 64, 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nERROR: warm-up failed — {ex.Message}");
            Console.Error.WriteLine($"       Is LM Studio running at {config.BaseUrl} with a model loaded?");
            return 1;
        }
        Console.WriteLine("Warm-up done.");

        // ----------------------------------------------------------------------------
        // PRE-FLIGHT CHECK
        //
        // Three things must be verified BEFORE measuring. Every Phase 1 bug came from
        // "I assumed".
        //
        //   1. Is response_format actually honoured? If the server ignores it, the
        //      C modes are silently mode A and the experiment measures nothing.
        //   2. Does the enum constraint bind? A schema that is accepted but not
        //      enforced looks identical in the happy path. The whole strict-vs-escape
        //      comparison rests on the enum genuinely binding.
        //   3. Does constrained decoding also apply to the reasoning channel? If it
        //      does, any quality difference may come from suppressed thinking rather
        //      than from the schema.
        // ----------------------------------------------------------------------------

        Header("PRE-FLIGHT CHECK — is response_format actually enforced?");

        // A prompt that would NATURALLY produce prose. If the schema is enforced we get
        // JSON anyway; if it is ignored we get a sentence.
        var probeSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": { "cevap": { "type": "string" } },
          "required": ["cevap"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

        var probe = await client.ChatAsync(
            [new Message("user", "Bugün hava nasıl? Bir paragraf yaz.")],
            maxTokens: config.MaxTokens, temperature: 0, schema: probeSchema);

        var probeJson = JsonExtract.TryParse(probe.Text, out var probeDoc);
        probeDoc?.Dispose();

        Console.WriteLine($"response_format probe : valid JSON = {probeJson}, " +
                          $"reasoning = {probe.ReasoningTokens} tok, finish = {probe.FinishReason}");

        if (!probeJson)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("ERROR: response_format is NOT being enforced — the model returned prose.");
            Console.Error.WriteLine("       The C modes would be identical to mode A and the experiment would be void.");
            Console.Error.WriteLine("       Check LM Studio > model settings > Structured Output, or upgrade the server.");
            return 1;
        }

        // Does the enum bind? We ask for a value that is not in the enum. If the
        // constraint is real the model CANNOT emit it.
        var enumProbeSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": { "para_birimi": { "type": "string", "enum": ["TRY", "USD", "EUR"] } },
          "required": ["para_birimi"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

        var enumProbe = await client.ChatAsync(
            [new Message("user", "Para birimi Japon Yeni (JPY). JSON döndür.")],
            maxTokens: config.MaxTokens, temperature: 0, schema: enumProbeSchema);

        var enumValue = JsonExtract.GetString(enumProbe.Text, "para_birimi");
        var enumBinds = enumValue is "TRY" or "USD" or "EUR";

        Console.WriteLine($"enum constraint probe : returned '{enumValue ?? "(none)"}' — " +
                          $"{(enumBinds ? "ENUM BINDS" : "ENUM DOES NOT BIND")}");

        if (!enumBinds)
            Console.WriteLine("  WARNING: the schema is accepted but the enum is not enforced. Constrained\n" +
                              "           decoding is partial, so the strict-vs-escape comparison measures\n" +
                              "           wording rather than grammar. Say so in the write-up.");

        Console.WriteLine($"reasoning under schema: {probe.ReasoningTokens} tokens " +
                          $"({(probe.ReasoningTokens > 0 ? "thinking still happens" : "thinking suppressed — confounder!")})");

        // ----------------------------------------------------------------------------
        // The modes
        //
        // A_prose has no contract at all, so it is schema-independent and runs once.
        // Each selected variant contributes a described arm and an enforced arm: the
        // pair isolates ENFORCEMENT, and the strict/escape pair isolates DESIGN.
        // ----------------------------------------------------------------------------

        var variants = config.Schema switch
        {
            "strict" => new[] { task.Strict },
            "escape" => [task.Escape],
            _ => [task.Strict, task.Escape],
        };

        var modes = new List<(string Name, string Prompt, JsonElement? Schema, SchemaVariant? Variant)>
        {
            ("A_prose",
             $"{task.SourceText}\n\nBu metinden sözleşme bilgilerini JSON olarak çıkar. Sadece JSON döndür.",
             null, null),
        };

        foreach (var v in variants)
        {
            var prompt = $"{task.SourceText}\n\n{v.Description}\n\nSadece JSON döndür, açıklama yazma.";
            modes.Add(($"B_{v.Name}_described", prompt, null, v));
            modes.Add(($"C_{v.Name}_enforced", prompt, v.JsonSchema, v));
        }

        var totalCalls = modes.Count * config.N;
        Console.WriteLine($"\n{modes.Count} modes x {config.N} runs = {totalCalls} calls.");
        Console.WriteLine("The hard task averaged ~60 s per call in run 5, so budget accordingly.");

        var rows = new List<string[]>();
        var samples = new List<string>();
        var summary = new List<(string Mode, double ParseRate, double FieldNames, int KeySets,
                                double MeanAcc, int Halluc, double LatencyP50, double CompTok)>();

        foreach (var (modeName, prompt, schema, variant) in modes)
        {
            Header($"MODE {modeName}");

            var parsedOk = 0;
            var truncated = 0;
            var wrapped = 0;              // JSON surrounded by prose or fenced in ```
            var latencies = new List<double>();
            var completionTokens = new List<double>();
            var reasoningTokens = new List<double>();
            var promptTokens = new List<double>();

            var fieldsPresent = 0;        // requested keys found, summed over parsed runs
            var keySets = new List<string>();   // the full key set of each parsed run
            var escapeUsed = 0;           // runs that listed anything in bulunamayan_alanlar

            // field -> normalized value, one entry per PARSED run. A run that did not
            // parse has no field values at all; parse failure is reported once, by
            // parseRate, and must not also count as a stable "not found" answer.
            var perField = fields.ToDictionary(f => f, _ => new List<string>());

            for (var i = 0; i < config.N; i++)
            {
                var r = await client.ChatAsync([new Message("user", prompt)],
                    config.MaxTokens, config.Temperature, schema: schema);

                latencies.Add(r.TotalMs);
                completionTokens.Add(r.CompletionTokens);
                reasoningTokens.Add(r.ReasoningTokens);
                promptTokens.Add(r.PromptTokens);

                if (r.FinishReason == "length") truncated++;

                // "Wrapped" means valid JSON surrounded by prose or a ``` fence. Mode A
                // does this often; in a real pipeline it is the single biggest source of
                // parse failures, so it is counted separately from an outright failure.
                var raw = r.Text.Trim();
                var isClean = raw.StartsWith('{') && raw.EndsWith('}');

                if (JsonExtract.TryParse(raw, out var doc))
                {
                    parsedOk++;
                    if (!isClean) wrapped++;

                    using (doc)
                    {
                        var root = doc!.RootElement;

                        foreach (var f in fields)
                            perField[f].Add(Normalize(JsonExtract.GetNormalized(root, f)));

                        // Without a schema description the model invents its own key names
                        // (imza_tarihi, firma_adi, toplam_bedel...): valid JSON, wrong
                        // contract. Without this count it showed up as accuracy 0% next to
                        // a misleading 90-100% consistency of "not found".
                        fieldsPresent += fields.Count(f => root.TryGetProperty(f, out _));

                        keySets.Add(string.Join("|", root.EnumerateObject()
                            .Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal)));

                        // Did the model take the escape hatch when it had one? This is the
                        // whole point of the escape variant: a contract that permits
                        // "I cannot answer this" should get used, not ignored.
                        if (root.TryGetProperty(EscapeField, out var esc)
                            && esc.ValueKind == JsonValueKind.Array
                            && esc.GetArrayLength() > 0)
                            escapeUsed++;
                    }
                }
                else
                {
                    doc?.Dispose();
                }

                // Every output reaches disk, not just the first few. Phase 1 lost 17 of 20
                // outputs that way; a metric bug found later can only be corrected by
                // recomputing from the raw outputs.
                samples.Add($"===== {modeName} — run {i + 1}/{config.N} " +
                            $"(finish={r.FinishReason}, reasoning={r.ReasoningTokens} tok) =====\n{raw}\n");
            }

            // --- metrics ---
            var parseRate = 100.0 * parsedOk / config.N;

            // Consistency: across the parsed runs, how often does a field take its most
            // common value? Grouped on the NORMALIZED value, like accuracy: grouping the
            // raw string split 2750000 and 2750000.0 into two answers and showed 40%
            // consistency next to 65% accuracy.
            var consistency = fields.ToDictionary(f => f, f => parsedOk == 0 ? -1 :
                100.0 * perField[f].GroupBy(v => v).Max(g => g.Count()) / parsedOk);

            var accuracy = fields.ToDictionary(f => f, f => parsedOk == 0 ? -1 :
                100.0 * perField[f].Count(v => v == Normalize(groundTruth[f])) / parsedOk);

            var fieldNameRate = parsedOk == 0 ? -1 : 100.0 * fieldsPresent / (parsedOk * fields.Length);

            // Schema stability: 1 means every parsed run used the same key set; parsedOk
            // means every run invented its own. Ordinal, so sirket_adi and şirket_adi are
            // different contracts, as they are to a deserializer.
            var distinctKeySets = keySets.Distinct(StringComparer.Ordinal).Count();
            var keySetsText = parsedOk == 0 ? "n/a" : $"{distinctKeySets}/{parsedOk}";

            // Hallucination: every field whose truthful answer is "the text does not say".
            // Anything other than an explicit null is an answer the text cannot support.
            // A missing key is a contract miss (see field names), not an invention.
            var hallucinated = fields.Where(x => Normalize(groundTruth[x]) == ExplicitNull)
                .ToDictionary(x => x, x => perField[x].Count(v => v != ExplicitNull && v != Missing));

            var meanAcc = parsedOk == 0 ? -1 : fields.Average(f => accuracy[f]);

            Console.WriteLine($"parse success   : {parseRate,6:F1}%   ({parsedOk}/{config.N})");
            Console.WriteLine($"wrapped in prose: {wrapped,6}       (valid JSON but not a bare object)");
            Console.WriteLine($"truncated       : {truncated,6}       (finish_reason = length)");
            Console.WriteLine($"field names     : {Pct(fieldNameRate),7}   (requested keys present in parsed runs)");
            Console.WriteLine($"key sets        : {keySetsText,7}   (distinct schemas across parsed runs; 1 = a contract)");
            if (variant?.HasEscapeHatch == true)
                Console.WriteLine($"escape hatch    : {escapeUsed,6}/{parsedOk}   " +
                                  $"(runs that listed a field in '{EscapeField}')");
            Console.WriteLine($"latency p50     : {Stats.Median(latencies),6:F0} ms");
            Console.WriteLine($"completion tok  : {completionTokens.Average(),6:F0}   " +
                              $"(reasoning: {reasoningTokens.Average():F0})");

            if (truncated > 0)
                Console.WriteLine($"  WARNING: {truncated}/{config.N} runs hit max_tokens={config.MaxTokens}. A cut-off run\n" +
                                  "           cannot parse, so parse success is partly measuring the thinking\n" +
                                  "           budget here, not the schema. Raise --max-tokens before comparing modes.");

            Console.WriteLine();
            Console.WriteLine($"{"field",-14} {"consistency",12} {"accuracy",10}   (over {parsedOk} parsed runs)");
            Console.WriteLine(new string('-', 40));
            foreach (var f in fields)
                Console.WriteLine($"{f,-14} {Pct(consistency[f]),12} {Pct(accuracy[f]),10}");
            Console.WriteLine(new string('-', 40));
            Console.WriteLine($"{"MEAN",-14} {"",12} {Pct(meanAcc),10}");

            foreach (var (f, count) in hallucinated)
                Console.WriteLine($"HALLUCINATION   : {count}/{parsedOk} parsed runs answered '{f}', " +
                                  "which the source text does not contain");

            // Where a field is not always right, show WHAT came back: a stable wrong
            // value, a guessed date and an honest null read very differently.
            foreach (var f in fields.Where(x => accuracy[x] is >= 0 and < 100))
                Console.WriteLine($"  {f,-14}: " + string.Join(", ", perField[f]
                    .GroupBy(v => v).OrderByDescending(g => g.Count()).Take(4)
                    .Select(g => $"{Show(g.Key)} x{g.Count()}")));

            summary.Add((modeName, parseRate, fieldNameRate, distinctKeySets, meanAcc,
                         hallucinated.Values.Sum(), Stats.Median(latencies), completionTokens.Average()));

            foreach (var f in fields)
            {
                rows.Add([runId, client.ModelId, modeName, f,
                          Inv(consistency[f], "F1"), Inv(accuracy[f], "F1"),
                          Inv(parseRate, "F1"), parsedOk.ToString(), config.N.ToString(),
                          wrapped.ToString(), truncated.ToString(),
                          Inv(Stats.Median(latencies), "F1"),
                          Inv(Stats.Percentile(latencies, 95), "F1"),
                          Inv(completionTokens.Average(), "F1"),
                          Inv(reasoningTokens.Average(), "F1"),
                          Inv(config.Temperature, "F2"),
                          hallucinated.ContainsKey(f) ? hallucinated[f].ToString() : "",
                          perField[f].GroupBy(v => v)
                                     .OrderByDescending(g => g.Count())
                                     .Select(g => Show(g.Key)).FirstOrDefault() ?? "",
                          Inv(fieldNameRate, "F1"),
                          config.MaxTokens.ToString(),
                          parsedOk == 0 ? "-1" : distinctKeySets.ToString(),
                          task.Name,
                          Inv(promptTokens.Average(), "F1"),
                          variant?.Name ?? "none",
                          variant?.HasEscapeHatch == true ? escapeUsed.ToString() : "",
                          Inv(meanAcc, "F1")]);
            }
        }

        // ----------------------------------------------------------------------------
        // Cross-mode summary — the point of the whole experiment sits in this table.
        // ----------------------------------------------------------------------------

        Header("SUMMARY — all modes");
        Console.WriteLine($"{"mode",-22} {"parse",7} {"fields",7} {"keysets",8} {"mean acc",9} {"halluc",7} {"p50 ms",8} {"comp tok",9}");
        Console.WriteLine(new string('-', 82));
        foreach (var s in summary)
            Console.WriteLine($"{s.Mode,-22} {Pct(s.ParseRate),7} {Pct(s.FieldNames),7} " +
                              $"{s.KeySets,8} {Pct(s.MeanAcc),9} {s.Halluc,7} {s.LatencyP50,8:F0} {s.CompTok,9:F0}");

        Console.WriteLine();
        Console.WriteLine("HOW TO READ IT:");
        Console.WriteLine("  A vs B           -> what a written contract buys (key names, one schema)");
        Console.WriteLine("  B vs C, same variant -> what GRAMMAR buys on top of describing the same contract");
        Console.WriteLine("  strict vs escape -> what the DESIGN of the contract buys, at equal enforcement");
        Console.WriteLine("  comp tok         -> the cost side: grammar is not free, and neither is thinking");
        Console.WriteLine();
        Console.WriteLine("  parse success -> does the output survive a JSON.parse at all");
        Console.WriteLine("  field names   -> are the requested keys there (if not, accuracy 0% means");
        Console.WriteLine("                   'not found', not 'wrong')");
        Console.WriteLine("  key sets      -> how many different schemas the parsed runs produced (1 = a contract)");
        Console.WriteLine("  consistency   -> same value across runs. HIGH consistency with LOW accuracy is");
        Console.WriteLine("                   the worst outcome: a stable wrong answer survives testing");
        Console.WriteLine("  hallucination -> a field the text cannot answer must come back null");
        Console.WriteLine("  Consistency, accuracy, field names and hallucination count PARSED runs only.");

        if (task.Name == "hard")
        {
            Console.WriteLine();
            Console.WriteLine("HARD TASK, STRICT VARIANT: the truth of para_birimi (CHF) is outside the enum,");
            Console.WriteLine("       and tarih / teminat_orani are required but unanswerable. C_strict's grammar");
            Console.WriteLine("       can emit none of the three true answers, so its accuracy there is 0% BY");
            Console.WriteLine("       CONSTRUCTION: read those rows as 'what the contract forced the model to");
            Console.WriteLine("       say'. The escape variant is the control that separates 'enforcement is");
            Console.WriteLine("       harmful' from 'this contract was badly designed'.");
        }

        Console.WriteLine();
        Console.WriteLine("LIMIT: a schema constrains SYNTAX, not SEMANTICS. A perfectly schema-compliant");
        Console.WriteLine("       object can contain an entirely invented number — see the hallucination");
        Console.WriteLine("       column, and Experiment 3 (citation verification).");

        Csv.Write(config, $"p2_01_{task.Name}_structured_output",
            ["run_id", "model", "mode", "field", "consistency_pct", "accuracy_pct",
             "parse_success_pct", "parsed_ok", "n", "wrapped_in_prose", "truncated",
             "latency_p50_ms", "latency_p95_ms", "avg_completion_tokens",
             "avg_reasoning_tokens", "temperature", "hallucinated_count", "modal_value",
             "field_name_match_pct", "max_tokens", "distinct_key_sets", "task",
             "avg_prompt_tokens", "schema_variant", "escape_hatch_used", "mean_accuracy_pct"],
            rows);

        File.WriteAllText(Path.Combine(config.OutputDir, $"p2_01_{task.Name}_samples.{runId}.txt"),
            string.Join("\n", samples), Encoding.UTF8);

        Console.WriteLine($"\nDone. Outputs: {Path.GetFullPath(config.OutputDir)}");
        return 0;
    }


    // ============================================================================
    // The extraction tasks
    //
    // Turkish on purpose: this is the real workload language and Phase 1 measured a
    // 1.81x worst-case token penalty for it. Measuring structured output on English
    // text would understate both the cost and the difficulty.
    //
    // Ground truth is written by hand. "null" means the truthful answer is the key
    // PRESENT with an explicit JSON null; a missing key is a different answer (see
    // Normalize). Comparing against C# null used to score every correct null as a
    // miss and every unparsed run as a hit.
    // ============================================================================

    // The escape-hatch field. Not scored — it is an affordance, not an answer — but
    // we count how often the model uses it.
    const string EscapeField = "bulunamayan_alanlar";

    static class Tasks
    {
        // EASY — every field is stated plainly. The only trap is `ceza_orani`, which
        // does not appear in the text. Prompts are byte-identical to runs 2-4 so the
        // results stay comparable with them.
        public static readonly ExtractionTask Easy = new(
            Name: "easy",
            SourceText:
                "15.03.2026 tarihinde imzalanan hizmet sözleşmesi kapsamında, Aydın Teknoloji A.Ş. " +
                "tarafından 18 ay süreyle bakım hizmeti verilecektir. Sözleşme bedeli 2.750.000,00 TL " +
                "olarak belirlenmiş olup, ödemeler üçer aylık dönemler halinde yapılacaktır.",
            Fields: ["tarih", "taraf", "sure_ay", "tutar", "para_birimi", "ceza_orani"],
            GroundTruth: new()
            {
                ["tarih"] = "2026-03-15",
                ["taraf"] = "Aydın Teknoloji A.Ş.",
                ["sure_ay"] = "18",
                ["tutar"] = "2750000",
                ["para_birimi"] = "TRY",
                ["ceza_orani"] = "null",      // NOT in the text: explicit JSON null
            },
            Strict: new SchemaVariant(
                Name: "strict",
                HasEscapeHatch: false,
                Description: """
                    Şu alanları içeren bir JSON nesnesi döndür:
                    - tarih: string, ISO 8601 formatında (YYYY-MM-DD)
                    - taraf: string, hizmeti veren tarafın tam adı
                    - sure_ay: number, sözleşme süresi ay cinsinden
                    - tutar: number, sözleşme bedeli (ondalık ayırıcı nokta, binlik ayırıcı yok)
                    - para_birimi: string, şunlardan biri: TRY, USD, EUR
                    - ceza_orani: number veya null, gecikme cezası oranı
                    """,
                JsonSchema: Schema("""
                    {
                      "type": "object",
                      "properties": {
                        "tarih":        { "type": "string" },
                        "taraf":        { "type": "string" },
                        "sure_ay":      { "type": "number" },
                        "tutar":        { "type": "number" },
                        "para_birimi":  { "type": "string", "enum": ["TRY", "USD", "EUR"] },
                        "ceza_orani":   { "type": ["number", "null"] }
                      },
                      "required": ["tarih", "taraf", "sure_ay", "tutar", "para_birimi", "ceza_orani"],
                      "additionalProperties": false
                    }
                    """)),
            Escape: new SchemaVariant(
                Name: "escape",
                HasEscapeHatch: true,
                Description: """
                    Şu alanları içeren bir JSON nesnesi döndür. Metinde bulunmayan bir bilgi
                    için DEĞER UYDURMA: alanı null bırak ve adını bulunamayan_alanlar
                    dizisine ekle.
                    - tarih: string veya null, ISO 8601 formatında (YYYY-MM-DD)
                    - taraf: string veya null, hizmeti veren tarafın tam adı
                    - sure_ay: number veya null, sözleşme süresi ay cinsinden
                    - tutar: number veya null, sözleşme bedeli (ondalık ayırıcı nokta, binlik ayırıcı yok)
                    - para_birimi: string veya null, şunlardan biri: TRY, USD, EUR, CHF, GBP, JPY, DIGER
                    - ceza_orani: number veya null, gecikme cezası oranı
                    - bulunamayan_alanlar: string dizisi, metinden çıkarılamayan alanların adları
                    """,
                JsonSchema: Schema("""
                    {
                      "type": "object",
                      "properties": {
                        "tarih":        { "type": ["string", "null"] },
                        "taraf":        { "type": ["string", "null"] },
                        "sure_ay":      { "type": ["number", "null"] },
                        "tutar":        { "type": ["number", "null"] },
                        "para_birimi":  { "type": ["string", "null"],
                                          "enum": ["TRY", "USD", "EUR", "CHF", "GBP", "JPY", "DIGER", null] },
                        "ceza_orani":   { "type": ["number", "null"] },
                        "bulunamayan_alanlar": { "type": "array", "items": { "type": "string" } }
                      },
                      "required": ["tarih", "taraf", "sure_ay", "tutar", "para_birimi",
                                   "ceza_orani", "bulunamayan_alanlar"],
                      "additionalProperties": false
                    }
                    """)));

        // HARD — the same contract and the same facts, written the way real documents
        // are. What each trap measures:
        //   tutar          "iki milyon yedi yüz elli bin"   -> 2750000 (words to number)
        //   sure_ay        "bir buçuk yıl"                  -> 18 (unit conversion)
        //   tarih          "Mart ayının ortasında", no year -> no ISO date exists, so the
        //                  truthful answer is null; "2024-03-15" is invented precision
        //   para_birimi    İsviçre Frangı                   -> CHF. Outside the STRICT
        //                  enum, inside the ESCAPE one: the same fact is answerable
        //                  under one contract and not the other
        //   ceza_orani     absent, nullable in both         -> null (the control)
        //   teminat_orani  absent; required and NOT nullable under strict, nullable
        //                  under escape -> the pair shows whether a contract lets an
        //                  unanswerable field stay null or forces an invention
        public static readonly ExtractionTask Hard = new(
            Name: "hard",
            SourceText:
                "Hizmet sözleşmesi Mart ayının ortasında imzalanmış olup, Aydın Teknoloji A.Ş. " +
                "tarafından bir buçuk yıl süreyle bakım hizmeti verilecektir. Sözleşme bedeli " +
                "iki milyon yedi yüz elli bin İsviçre Frangı olarak belirlenmiş olup, ödemeler " +
                "üçer aylık dönemler halinde yapılacaktır.",
            Fields: ["tarih", "taraf", "sure_ay", "tutar", "para_birimi", "ceza_orani", "teminat_orani"],
            GroundTruth: new()
            {
                ["tarih"] = "null",           // vague and no year: no ISO date exists
                ["taraf"] = "Aydın Teknoloji A.Ş.",
                ["sure_ay"] = "18",
                ["tutar"] = "2750000",
                ["para_birimi"] = "CHF",
                ["ceza_orani"] = "null",      // NOT in the text
                ["teminat_orani"] = "null",   // NOT in the text
            },
            Strict: new SchemaVariant(
                Name: "strict",
                HasEscapeHatch: false,
                Description: """
                    Şu alanları içeren bir JSON nesnesi döndür:
                    - tarih: string, ISO 8601 formatında (YYYY-MM-DD)
                    - taraf: string, hizmeti veren tarafın tam adı
                    - sure_ay: number, sözleşme süresi ay cinsinden
                    - tutar: number, sözleşme bedeli (ondalık ayırıcı nokta, binlik ayırıcı yok)
                    - para_birimi: string, şunlardan biri: TRY, USD, EUR
                    - ceza_orani: number veya null, gecikme cezası oranı
                    - teminat_orani: number, kesin teminat oranı (yüzde)
                    """,
                JsonSchema: Schema("""
                    {
                      "type": "object",
                      "properties": {
                        "tarih":          { "type": "string" },
                        "taraf":          { "type": "string" },
                        "sure_ay":        { "type": "number" },
                        "tutar":          { "type": "number" },
                        "para_birimi":    { "type": "string", "enum": ["TRY", "USD", "EUR"] },
                        "ceza_orani":     { "type": ["number", "null"] },
                        "teminat_orani":  { "type": "number" }
                      },
                      "required": ["tarih", "taraf", "sure_ay", "tutar", "para_birimi",
                                   "ceza_orani", "teminat_orani"],
                      "additionalProperties": false
                    }
                    """)),
            Escape: new SchemaVariant(
                Name: "escape",
                HasEscapeHatch: true,
                Description: """
                    Şu alanları içeren bir JSON nesnesi döndür. Metinde bulunmayan veya
                    kesin olarak çıkarılamayan bir bilgi için DEĞER UYDURMA: alanı null
                    bırak ve adını bulunamayan_alanlar dizisine ekle.
                    - tarih: string veya null, ISO 8601 formatında (YYYY-MM-DD). Yıl
                      belirtilmemişse veya tarih belirsizse null.
                    - taraf: string veya null, hizmeti veren tarafın tam adı
                    - sure_ay: number veya null, sözleşme süresi ay cinsinden
                    - tutar: number veya null, sözleşme bedeli (ondalık ayırıcı nokta, binlik ayırıcı yok)
                    - para_birimi: string veya null, şunlardan biri: TRY, USD, EUR, CHF, GBP, JPY, DIGER
                    - ceza_orani: number veya null, gecikme cezası oranı
                    - teminat_orani: number veya null, kesin teminat oranı (yüzde)
                    - bulunamayan_alanlar: string dizisi, metinden çıkarılamayan alanların adları
                    """,
                JsonSchema: Schema("""
                    {
                      "type": "object",
                      "properties": {
                        "tarih":          { "type": ["string", "null"] },
                        "taraf":          { "type": ["string", "null"] },
                        "sure_ay":        { "type": ["number", "null"] },
                        "tutar":          { "type": ["number", "null"] },
                        "para_birimi":    { "type": ["string", "null"],
                                            "enum": ["TRY", "USD", "EUR", "CHF", "GBP", "JPY", "DIGER", null] },
                        "ceza_orani":     { "type": ["number", "null"] },
                        "teminat_orani":  { "type": ["number", "null"] },
                        "bulunamayan_alanlar": { "type": "array", "items": { "type": "string" } }
                      },
                      "required": ["tarih", "taraf", "sure_ay", "tutar", "para_birimi",
                                   "ceza_orani", "teminat_orani", "bulunamayan_alanlar"],
                      "additionalProperties": false
                    }
                    """)));

        static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
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

    // A missing key and an explicit JSON null are DIFFERENT answers and get
    // different markers. Both used to collapse into "" or "null" inconsistently,
    // which scored every correct null as wrong. The NUL prefix keeps a marker from
    // colliding with a real string value.
    const string Missing = "\u0000missing";
    const string ExplicitNull = "\u0000null";

    // Loose comparison so that formatting differences do not count as errors:
    // "2750000" and "2750000.00" are the same amount. What we measure is
    // extraction, not number formatting.
    // NOTE: a Turkish-formatted "2.750.000,00" does NOT parse here (invariant
    // culture) and stays a string, so it counts as a miss — which is correct, a
    // consumer cannot deserialize it into a number either.
    static string Normalize(string? v)
    {
        if (v is null) return Missing;
        var s = v.Trim().Trim('"');
        if (s.Length == 0) return Missing;
        if (s.Equals("null", StringComparison.OrdinalIgnoreCase)) return ExplicitNull;
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d.ToString("0.####", CultureInfo.InvariantCulture);
        return s.ToLowerInvariant();
    }

    static string Show(string normalized) => normalized switch
    {
        Missing => "(missing)",
        ExplicitNull => "null",
        _ => normalized,
    };

    // -1 = could not be measured (no run parsed); printed as n/a, never as 0%.
    static string Pct(double v) => v < 0 ? "n/a" : Inv(v, "F0") + "%";


    // ============================================================================
    // JSON extraction
    //
    // Models wrap JSON in ``` fences or surround it with prose. A pipeline that
    // only calls JsonDocument.Parse on the raw text reports failures that are
    // really formatting issues. We separate the two: TryParse salvages what it can,
    // and the caller counts "wrapped" separately from "failed".
    // ============================================================================

    static class JsonExtract
    {
        public static bool TryParse(string raw, out JsonDocument? doc)
        {
            doc = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var text = raw.Trim();

            if (text.StartsWith("```"))
            {
                var firstNewline = text.IndexOf('\n');
                if (firstNewline > 0) text = text[(firstNewline + 1)..];
                var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0) text = text[..lastFence];
                text = text.Trim();
            }

            if (!text.StartsWith('{'))
            {
                var start = text.IndexOf('{');
                var end = text.LastIndexOf('}');
                if (start < 0 || end <= start) return false;
                text = text[start..(end + 1)];
            }

            try { doc = JsonDocument.Parse(text); return true; }
            catch (JsonException) { return false; }
        }

        public static string? GetString(string raw, string field)
        {
            if (!TryParse(raw, out var doc) || doc is null) return null;
            using (doc)
                return doc.RootElement.TryGetProperty(field, out var v) ? v.ToString() : null;
        }

        /// <summary>
        /// Reads a field and normalizes it to a comparable string. Returns "null" for
        /// an explicit JSON null and null for a missing property — different answers
        /// that must not be collapsed.
        /// </summary>
        public static string? GetNormalized(JsonElement root, string field)
        {
            if (!root.TryGetProperty(field, out var v)) return null;
            return v.ValueKind == JsonValueKind.Null ? "null" : v.ToString();
        }
    }


    // ============================================================================
    // LM Studio client
    // ============================================================================

    sealed class LmClient(HttpClient http, string baseUrl, string modelId)
    {
        public string ModelId { get; } = modelId;

        static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public async Task<ChatResult> ChatAsync(
            List<Message> messages, int maxTokens, double temperature,
            int? seed = null, JsonElement? schema = null)
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = ModelId,
                ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                ["max_tokens"] = maxTokens,
                ["temperature"] = temperature,
                ["stream"] = false,
            };

            if (seed.HasValue) body["seed"] = seed.Value;

            // OpenAI-compatible structured output. The server compiles this into a
            // grammar and constrains decoding to it.
            if (schema.HasValue)
            {
                body["response_format"] = new Dictionary<string, object>
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new Dictionary<string, object>
                    {
                        ["name"] = "extraction",
                        ["strict"] = true,
                        ["schema"] = schema.Value,
                    },
                };
            }

            var sw = Stopwatch.StartNew();

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json")
            };

            using var res = await http.SendAsync(req);
            var raw = await res.Content.ReadAsStringAsync();
            sw.Stop();

            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"{(int)res.StatusCode} — {Trunc(raw, 400)}");

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // Some OpenAI-compatible servers return an error with HTTP 200.
            if (root.TryGetProperty("error", out var errEl))
                throw new InvalidOperationException($"server error (HTTP 200) — {Trunc(errEl.ToString(), 400)}");

            var text = "";
            var reasoning = "";
            var finishReason = "";

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                if (choices[0].TryGetProperty("message", out var msg))
                {
                    text = ReadStr(msg, "content") ?? "";
                    reasoning = ReadStr(msg, "reasoning_content") ?? ReadStr(msg, "reasoning") ?? "";
                }
                finishReason = ReadStr(choices[0], "finish_reason") ?? "";
            }

            var (pt, ct, rt) = ReadUsage(root);
            return new ChatResult(text, reasoning, pt, ct, rt, sw.Elapsed.TotalMilliseconds, finishReason);
        }

        static string? ReadStr(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static (int, int, int) ReadUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
                return (0, 0, 0);

            var r = 0;
            if (u.TryGetProperty("completion_tokens_details", out var d) && d.ValueKind == JsonValueKind.Object)
                r = ReadInt(d, "reasoning_tokens");

            return (ReadInt(u, "prompt_tokens"), ReadInt(u, "completion_tokens"), r);

            static int ReadInt(JsonElement el, string name)
            {
                if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
                if (v.TryGetInt32(out var i)) return i;
                if (v.TryGetDouble(out var dd) && dd >= 0 && dd <= int.MaxValue) return (int)Math.Round(dd);
                return 0;
            }
        }

        static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "...";
    }

    record Message(string Role, string Content);

    record ChatResult(
        string Text,
        string Reasoning,
        int PromptTokens,
        int CompletionTokens,
        int ReasoningTokens,
        double TotalMs,
        string FinishReason);

    // One contract, in the two forms the experiment needs: prose (mode B) and a JSON
    // schema (mode C). Keeping them in one record is what makes B and C differ only
    // in ENFORCEMENT — if they drifted apart the comparison would silently become
    // one about wording.
    record SchemaVariant(
        string Name,
        bool HasEscapeHatch,
        string Description,
        JsonElement JsonSchema);

    record ExtractionTask(
        string Name,
        string SourceText,
        string[] Fields,
        Dictionary<string, string> GroundTruth,
        SchemaVariant Strict,
        SchemaVariant Escape);


    // ============================================================================
    // Statistics — median, not mean (Phase 1: GPU spikes distort the mean)
    // ============================================================================

    static class Stats
    {
        public static double Median(IEnumerable<double> values)
        {
            var s = values.OrderBy(v => v).ToArray();
            if (s.Length == 0) return 0;
            return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2.0;
        }

        public static double Percentile(IEnumerable<double> values, double p)
        {
            var s = values.OrderBy(v => v).ToArray();
            if (s.Length == 0) return 0;
            var idx = (int)Math.Ceiling(p / 100.0 * s.Length) - 1;
            return s[Math.Clamp(idx, 0, s.Length - 1)];
        }
    }


    // ============================================================================
    // CSV — appends when the schema matches so runs stay comparable
    // ============================================================================

    static class Csv
    {
        public static void Write(Config config, string name, string[] header, List<string[]> rows)
        {
            var path = Path.Combine(config.OutputDir, $"{name}.csv");
            var headerLine = string.Join(",", header.Select(Escape));
            var append = false;

            if (File.Exists(path))
            {
                var existing = File.ReadLines(path, new UTF8Encoding(true)).FirstOrDefault()?.TrimStart('\uFEFF');
                if (existing == headerLine) append = true;
                else
                {
                    File.Move(path, path + ".bak", overwrite: true);
                    Console.WriteLine($"   NOTE: schema of {name}.csv changed, old kept as {name}.csv.bak");
                }
            }

            var sb = new StringBuilder();
            if (!append) sb.AppendLine(headerLine);
            foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Escape)));

            if (append) File.AppendAllText(path, sb.ToString(), new UTF8Encoding(true));
            else File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));

            Console.WriteLine($"-> {path}{(append ? $" (+{rows.Count} rows)" : "")}");
        }

        static string Escape(string v)
        {
            v ??= "";
            if (v.Length > 0 && (v[0] is '=' or '+' or '@'))
                return "\"'" + v.Replace("\"", "\"\"") + "\"";
            var needsQuote = v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r');
            return needsQuote ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
    }


    // ============================================================================
    // Configuration
    // ============================================================================

    sealed class Config
    {
        public string BaseUrl { get; init; } = "http://localhost:1234/v1";
        public string ModelId { get; init; } = "";
        // Its own folder: Phase 1 writes to results/ and the phases must not mix.
        public string OutputDir { get; init; } = Path.Combine("results", "step2");
        public int N { get; init; } = 20;
        public double Temperature { get; init; } = 0.7;

        // Generous on purpose. Phase 1 measured 1016 reasoning tokens for a
        // one-sentence answer; the hard extraction averaged ~6800 against a ceiling of
        // 8000 and cut off 5 of 60 runs. A cut-off run cannot parse, so a tight budget
        // turns "parse success" into a measurement of the thinking budget.
        public int MaxTokens { get; init; } = 12000;

        // hard by default: the easy task hit a ceiling (B and C both scored 100%).
        public string Task { get; init; } = "hard";

        // both by default: strict alone cannot tell "enforcement is harmful" from
        // "this contract was badly designed".
        public string Schema { get; init; } = "both";

        public const string Usage =
            "Usage: dotnet run -- 2 --model <id> [--url <address>] [--out <dir>] " +
            "[--n <count>] [--temp <value>] [--max-tokens <count>] [--task easy|hard] " +
            "[--schema strict|escape|both]";

        public static Config Parse(string[] args)
        {
            var url = "http://localhost:1234/v1";
            string? model = null;
            var outDir = Path.Combine("results", "step2");
            var n = 20;
            var temp = 0.7;
            var maxTokens = 12000;
            var task = "hard";
            var schema = "both";
            var errors = new List<string>();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg is "--url" or "--model" or "--out" or "--n" or "--temp"
                        or "--max-tokens" or "--task" or "--schema")
                {
                    if (i + 1 >= args.Length) { errors.Add($"{arg} expects a value"); break; }
                    var v = args[++i];
                    switch (arg)
                    {
                        case "--url": url = v; break;
                        case "--model": model = v; break;
                        case "--out": outDir = v; break;
                        case "--n":
                            if (!int.TryParse(v, out n) || n < 1) errors.Add($"--n must be a positive integer, got '{v}'");
                            break;
                        case "--temp":
                            if (!double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out temp) || temp < 0)
                                errors.Add($"--temp must be a non-negative number, got '{v}'");
                            break;
                        case "--max-tokens":
                            if (!int.TryParse(v, out maxTokens) || maxTokens < 1)
                                errors.Add($"--max-tokens must be a positive integer, got '{v}'");
                            break;
                        case "--task":
                            if (v is "easy" or "hard") task = v;
                            else errors.Add($"--task must be easy or hard, got '{v}'");
                            break;
                        case "--schema":
                            if (v is "strict" or "escape" or "both") schema = v;
                            else errors.Add($"--schema must be strict, escape or both, got '{v}'");
                            break;
                    }
                }
                else errors.Add($"unrecognized argument: '{arg}'");
            }

            if (string.IsNullOrWhiteSpace(model))
                errors.Add("--model is required (auto-discovery is off here: schema behaviour is " +
                           "model-specific and runs must be comparable)");

            if (errors.Count > 0)
                throw new ArgumentException(string.Join("\n  - ", errors.Prepend("Argument error:")));

            return new Config
            {
                BaseUrl = url.TrimEnd('/'),
                ModelId = model!,
                OutputDir = outDir,
                N = n,
                Temperature = temp,
                MaxTokens = maxTokens,
                Task = task,
                Schema = schema,
            };
        }
    }
}