// ============================================================================
// Faz 1 — LLM Mekanikleri Ölçüm Harness'ı v2 (Lokal / LM Studio)
//
// v1'de bulunan hata: Qwen3.5 bir reasoning modeli. Düşünme token'larını
// delta.content içinde DEĞİL, delta.reasoning_content içinde yayınlıyor.
// v1 sadece content'e baktığı için:
//   - tüm çıktılar boş göründü (Deney 3 ve 6)
//   - TTFT damgası üretimin SONUNA düştü, ttft == total oldu (Deney 2 ve 4)
//
// v2'de düzeltilenler:
//   1. reasoning_content ayrı kanal olarak okunuyor (stream + non-stream)
//   2. İki ayrı TTFT: TtftAny (fiziksel prefill) / TtftContent (algılanan gecikme)
//   3. enable_thinking=false ile ölçüm koşularında düşünme kapatılabiliyor
//      (+ /no_think fallback'i, ön kontrolle doğrulanıyor)
//   4. Deney 2 A kolu max_tokens 16 -> 64 (marjsız değildi)
//   5. Deney 4 uzun context'e taşındı (kısa prompt'ta KV cache etkisi gürültüdeydi)
//   6. Deney 5'e 16 paralellik seviyesi eklendi (eğri doymamıştı)
//   7. Deney 6 hem düşünme açık hem kapalı koşuyor
//
// Bağımlılık yok. Ham HttpClient kullanıyoruz çünkü abstraction, tam da
// ölçmek istediğimiz sağlayıcı farklarını normalize eder.
//
// Çalıştırma:
//   dotnet run -- --model qwen/qwen3.5-9b
//   dotnet run -- --model qwen/qwen3.5-9b 2 3 4
//   dotnet run -- --url http://localhost:1234/v1 --out results2 --doc-words 2000
// ============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var config = Config.Parse(args);
Console.OutputEncoding = Encoding.UTF8;

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
var client = new LmClient(http, config.BaseUrl, config.ModelId);

Directory.CreateDirectory(config.OutputDir);

Console.WriteLine($"Base URL : {config.BaseUrl}");
Console.WriteLine($"Model    : {(string.IsNullOrWhiteSpace(config.ModelId) ? "(otomatik keşif)" : config.ModelId)}");

if (string.IsNullOrWhiteSpace(config.ModelId))
{
    var models = await client.ListModelsAsync();
    if (models.Count == 0)
    {
        Console.Error.WriteLine("HATA: Sunucuda model yok. LM Studio > Developer > Status: Running ve model yüklü olmalı.");
        return 1;
    }

    Console.WriteLine("\nBulunan modeller:");
    foreach (var m in models) Console.WriteLine($"  - {m}");

    var chatCandidates = models
        .Where(m => !m.Contains("embed", StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (chatCandidates.Count == 0)
    {
        Console.Error.WriteLine("HATA: Chat modeli bulunamadı (hepsi embedding görünüyor).");
        return 1;
    }

    client.ModelId = chatCandidates[0];
    Console.WriteLine($"\nSeçilen chat modeli: {client.ModelId}");
}

var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
Console.WriteLine($"Run ID   : {runId}");

// ----------------------------------------------------------------------------
// ÖN KONTROL — reasoning kanalı ve enable_thinking desteği
//
// Bunu ölçümden ÖNCE doğruluyoruz. v1'in tüm hatası "varsaydım" yüzündendi.
// ----------------------------------------------------------------------------

Header("ÖN KONTROL — reasoning kanalı ve enable_thinking");

var probeOn = await client.ChatStreamAsync(
    [new Message("user", "Hakediş nedir? Tek cümle.")], maxTokens: 300, temperature: 0);

Console.WriteLine($"Düşünme AÇIK  : content={probeOn.Text.Length} kar, " +
                  $"reasoning={probeOn.Reasoning.Length} kar, " +
                  $"reasoning_tokens={probeOn.ReasoningTokens}, " +
                  $"ttftAny={probeOn.TtftAnyMs:F0}ms, ttftContent={Fmt(probeOn.TtftContentMs)}");

var probeOff = await client.ChatStreamAsync(
    [new Message("user", "Hakediş nedir? Tek cümle.")], maxTokens: 300, temperature: 0,
    disableThinking: true);

Console.WriteLine($"Düşünme KAPALI: content={probeOff.Text.Length} kar, " +
                  $"reasoning={probeOff.Reasoning.Length} kar, " +
                  $"reasoning_tokens={probeOff.ReasoningTokens}, " +
                  $"ttftAny={probeOff.TtftAnyMs:F0}ms, ttftContent={Fmt(probeOff.TtftContentMs)}");

var thinkingOff = probeOff.Reasoning.Length == 0 && probeOff.Text.Length > 0;

if (!thinkingOff)
{
    Console.WriteLine();
    Console.WriteLine("UYARI: enable_thinking=false etkisiz. /no_think fallback'i deneniyor...");
    client.UseNoThinkFallback = false;

    var probeOff2 = await client.ChatStreamAsync(
        [new Message("user", "Hakediş nedir? Tek cümle.")], maxTokens: 300, temperature: 0,
        disableThinking: true);

    Console.WriteLine($"Fallback ile  : content={probeOff2.Text.Length} kar, " +
                      $"reasoning={probeOff2.Reasoning.Length} kar");

    if (probeOff2.Reasoning.Length > 0)
    {
        Console.WriteLine("       Fallback da işe yaramadı. Ölçümler düşünme AÇIK koşacak.");
        Console.WriteLine("       README'ye bu notu düş — katsayılar reasoning dahil demektir.");
    }
}

if (probeOn.Text.Length == 0 && probeOn.Reasoning.Length == 0)
{
    Console.Error.WriteLine("\nHATA: Model hiç içerik üretmedi. Model yüklü mü, context ayarı doğru mu?");
    return 1;
}

// ----------------------------------------------------------------------------
// Warm-up — ölçüme dahil DEĞİL
//
// İlk çağrılar model yükleme, CUDA context init ve kernel derlemesini içerir.
// Gemini'de bu maliyet yoktu (sunucu zaten sıcaktı); lokalde en büyük outlier
// kaynağı bu.
// ----------------------------------------------------------------------------

Console.WriteLine("\nWarm-up (3 çağrı, ölçüme dahil değil)...");
for (var i = 0; i < 3; i++)
    await client.ChatAsync([new Message("user", "Merhaba.")], 16, 0, disableThinking: true);
Console.WriteLine("Warm-up tamam.");

// ----------------------------------------------------------------------------

var sel = config.Experiments;
if (sel.Contains(1)) await Experiment1_Tokenization(client, config, runId);
if (sel.Contains(2)) await Experiment2_PrefillDecode(client, config, runId);
if (sel.Contains(3)) await Experiment3_Determinism(client, config, runId);
if (sel.Contains(4)) await Experiment4_KvCache(client, config, runId);
if (sel.Contains(5)) await Experiment5_Concurrency(client, config, runId);
if (sel.Contains(6)) await Experiment6_QualitySpotCheck(client, config, runId);

Console.WriteLine($"\nTamamlandı. Çıktılar: {Path.GetFullPath(config.OutputDir)}");
return 0;


// ============================================================================
// DENEY 1 — Tokenization: Türkçe token cezası
//
// Gemini'de Türkçe İngilizceye göre 1.33x token tüketiyordu (BPE vocabulary
// bias). Qwen çok dilli veriyle eğitildi; çarpanın düşük olmasını bekliyoruz.
//
// YORUM UYARISI: prompt_tokens chat template overhead'ini de içerir (~15-20
// token, iki tarafta da sabit). Sabit terim hem payı hem paydayı şişirdiği için
// ölçülen oran gerçek metin-içi orandan DÜŞÜK çıkar. Bu deney alt sınırı verir.
//
// Neden önemli: KİK/hakediş dokümanları Türkçe. Çarpan, context'e kaç sayfa
// sözleşme sığdığını ve chunk boyutunu belirliyor.
// ============================================================================
static async Task Experiment1_Tokenization(LmClient client, Config config, string runId)
{
    Header("DENEY 1 — Tokenization: Türkçe token cezası");

    var pairs = new (string Name, string Tr, string En)[]
    {
        ("kisa_cumle",
         "Yüklenici, hakediş raporunu idareye sunmakla yükümlüdür.",
         "The contractor is obliged to submit the progress payment report to the administration."),

        ("teknik_paragraf",
         "İş artışı halinde, sözleşme bedelinin yüzde yirmisine kadar olan kısım için " +
         "yükleniciye ek süre verilir ve fiyat farkı hesaplaması güncel endeks değerleri " +
         "üzerinden yapılır. İdare, bu kararı gerekçeleriyle birlikte yazılı olarak bildirir.",
         "In case of a work increase, additional time is granted to the contractor for the portion " +
         "up to twenty percent of the contract price, and the price difference calculation is made " +
         "based on current index values. The administration notifies this decision in writing with its justification."),

        ("liste",
         "Belgeler: teklif mektubu, geçici teminat, iş deneyim belgesi, vergi borcu yoktur yazısı.",
         "Documents: bid letter, bid bond, work experience certificate, tax clearance letter."),

        ("sayisal",
         "Sözleşme bedeli 12.450.000,00 TL olup, ilk hakediş tutarı 1.245.000,00 TL'dir.",
         "The contract price is 12,450,000.00 TRY and the first progress payment is 1,245,000.00 TRY."),

        ("uzun_madde",
         "Sözleşmenin uygulanması sırasında ortaya çıkan ve yüklenicinin kusurundan " +
         "kaynaklanmayan gecikmelerde, idare tarafından yükleniciye süre uzatımı verilebilir. " +
         "Süre uzatımı talebi, gecikmeye neden olan olayın sona ermesinden itibaren yirmi gün " +
         "içinde yazılı olarak idareye bildirilir ve gerekçeleri belgelendirilir.",
         "During the execution of the contract, in case of delays not arising from the contractor's " +
         "fault, the administration may grant an extension of time to the contractor. The request " +
         "for extension of time shall be submitted to the administration in writing within twenty " +
         "days from the end of the event causing the delay, and its justifications shall be documented."),
    };

    var rows = new List<string[]>();
    Console.WriteLine($"{"metin",-18} {"TR tok",8} {"EN tok",8} {"oran",8} {"TR kar",8} {"EN kar",8}");
    Console.WriteLine(new string('-', 62));

    foreach (var (name, tr, en) in pairs)
    {
        // max_tokens=1 + düşünme kapalı: üretim maliyeti minimum,
        // sadece prompt_tokens'a bakıyoruz.
        var trRes = await client.ChatAsync([new Message("user", tr)], 1, 0, disableThinking: true);
        var enRes = await client.ChatAsync([new Message("user", en)], 1, 0, disableThinking: true);

        var ratio = (double)trRes.PromptTokens / Math.Max(enRes.PromptTokens, 1);

        Console.WriteLine($"{name,-18} {trRes.PromptTokens,8} {enRes.PromptTokens,8} {ratio,8:F3} {tr.Length,8} {en.Length,8}");

        rows.Add([
            runId, client.ModelId, name,
            trRes.PromptTokens.ToString(), enRes.PromptTokens.ToString(),
            Inv(ratio, "F4"),
            tr.Length.ToString(), en.Length.ToString(),
            Inv((double)tr.Length / Math.Max(trRes.PromptTokens, 1), "F3"),
            Inv((double)en.Length / Math.Max(enRes.PromptTokens, 1), "F3"),
        ]);
    }

    var avg = rows.Average(r => double.Parse(r[5], CultureInfo.InvariantCulture));
    var max = rows.Max(r => double.Parse(r[5], CultureInfo.InvariantCulture));

    Console.WriteLine(new string('-', 62));
    Console.WriteLine($"Ortalama TR/EN oranı: {avg:F3}   |   En kötü durum: {max:F3}");
    Console.WriteLine("Gemini referansı: 1.33. Chunk boyutu hesabını ORTALAMAYA değil");
    Console.WriteLine("EN KÖTÜ DURUMA göre yap — terim yoğun listeler en pahalı metin tipi.");

    Csv.Write(config, "01_tokenization",
        ["run_id", "model", "metin", "tr_tokens", "en_tokens", "oran",
         "tr_karakter", "en_karakter", "tr_kar_per_token", "en_kar_per_token"],
        rows);
}


// ============================================================================
// DENEY 2 — Prefill / Decode katsayı ayrıştırması
//
// Gemini'de model: ~1200ms sabit + ~3ms/token. O sabit maliyet ağ + kuyruktu.
// Lokalde ağ yok. Yerine iki farklı fiziksel süreç var:
//
//   prefill : input token'ları PARALEL işlenir (compute-bound, ucuz)
//   decode  : output token'ları SIRALI üretilir (memory-bandwidth-bound, pahalı)
//
// Doğru model:  total ≈ c + a*input_tokens + b*output_tokens
//
// v1 hatası: b'yi (total - ttft) üzerinden hesaplıyordu; ttft == total olduğu
// için sıfıra bölüyordu. v2 doğrudan total'i output_tokens'a regresyon ediyor —
// daha sağlam, ttft'ye bağımlı değil.
//
// Neden önemli: "prompt'u mu kısaltayım, çıktıyı mı" sorusunun sayısal cevabı.
// ============================================================================
static async Task Experiment2_PrefillDecode(LmClient client, Config config, string runId)
{
    Header("DENEY 2 — Prefill / Decode ayrıştırması");

    const int reps = 5;
    const int aOutTokens = 64;

    var rows = new List<string[]>();
    var inputSizes = new[] { 1, 20, 100, 400, 1200 };      // yaklaşık kelime
    var outputSizes = new[] { 32, 64, 128, 256, 512 };     // token

    Console.WriteLine($"A kolu — input DEĞİŞKEN, output sabit ({aOutTokens} token):");
    Console.WriteLine($"{"in_tok",8} {"out_tok",8} {"ttftAny",10} {"total_p50",10} {"total_p95",10}");
    Console.WriteLine(new string('-', 50));

    foreach (var size in inputSizes)
    {
        var samples = new List<StreamResult>();
        for (var i = 0; i < reps; i++)
        {
            // Benzersiz prefix: KV cache hit'ini KIRIYORUZ.
            // Kırmazsak ikinci çağrıdan itibaren prefill atlanır ve sahte bir
            // hızlanma ölçeriz. Cache etkisini Deney 4'te BİLEREK ölçüyoruz.
            var prompt = $"[{Guid.NewGuid():N}] " + Filler(size) + "\nBu metni tek kelimeyle özetle.";
            samples.Add(await client.ChatStreamAsync(
                [new Message("user", prompt)], aOutTokens, 0, disableThinking: true));
        }

        var ttft = Stats.Median(samples.Select(s => s.TtftAnyMs));
        var p50 = Stats.Median(samples.Select(s => s.TotalMs));
        var p95 = Stats.Percentile(samples.Select(s => s.TotalMs), 95);
        var inTok = (int)samples.Average(s => s.PromptTokens);
        var outTok = (int)samples.Average(s => s.CompletionTokens);

        Console.WriteLine($"{inTok,8} {outTok,8} {ttft,10:F1} {p50,10:F1} {p95,10:F1}");

        rows.Add([runId, client.ModelId, "A_prefill", inTok.ToString(), outTok.ToString(),
                  Inv(ttft, "F2"), Inv(p50, "F2"), Inv(p95, "F2")]);
    }

    Console.WriteLine("\nB kolu — input sabit (kısa), output DEĞİŞKEN:");
    Console.WriteLine($"{"in_tok",8} {"out_tok",8} {"ttftAny",10} {"total_p50",10} {"tok/s",10}");
    Console.WriteLine(new string('-', 50));

    foreach (var size in outputSizes)
    {
        var samples = new List<StreamResult>();
        for (var i = 0; i < reps; i++)
        {
            var prompt = $"[{Guid.NewGuid():N}] Şantiye güvenliği hakkında uzun bir metin yaz.";
            samples.Add(await client.ChatStreamAsync(
                [new Message("user", prompt)], size, 0.7, disableThinking: true));
        }

        var ttft = Stats.Median(samples.Select(s => s.TtftAnyMs));
        var p50 = Stats.Median(samples.Select(s => s.TotalMs));
        var p95 = Stats.Percentile(samples.Select(s => s.TotalMs), 95);
        var outTok = (int)samples.Average(s => s.CompletionTokens);
        var inTok = (int)samples.Average(s => s.PromptTokens);

        // Bölme koruması: ttft ile total çakışırsa (v1'deki hata) sonsuz yazma.
        var span = p50 - ttft;
        var tpsText = span > 1.0 ? $"{outTok / (span / 1000.0):F1}" : "n/a";

        Console.WriteLine($"{inTok,8} {outTok,8} {ttft,10:F1} {p50,10:F1} {tpsText,10}");

        rows.Add([runId, client.ModelId, "B_decode", inTok.ToString(), outTok.ToString(),
                  Inv(ttft, "F2"), Inv(p50, "F2"), Inv(p95, "F2")]);
    }

    // --- Katsayı çıkarımı ---
    // b (decode): B kolunda total'i output_tokens'a regresyon et.
    // a (prefill): A kolunda total'i input_tokens'a regresyon et.
    // A kolunun kesişimi b*64 + sabit overhead'i içerir; ayrıştırıyoruz.

    var aRows = rows.Where(r => r[2] == "A_prefill").ToList();
    var bRows = rows.Where(r => r[2] == "B_decode").ToList();

    var (b, bIntercept) = Stats.LinearFit(
        bRows.Select(r => (double)int.Parse(r[4])),
        bRows.Select(r => double.Parse(r[6], CultureInfo.InvariantCulture)));

    var (a, aIntercept) = Stats.LinearFit(
        aRows.Select(r => (double)int.Parse(r[3])),
        aRows.Select(r => double.Parse(r[6], CultureInfo.InvariantCulture)));

    var aAvgOut = aRows.Average(r => (double)int.Parse(r[4]));
    var pureOverhead = aIntercept - b * aAvgOut;
    var ratio = a > 1e-9 ? b / a : 0;

    Console.WriteLine(new string('-', 50));
    Console.WriteLine($"Prefill (a) : {a:F4} ms / input token");
    Console.WriteLine($"Decode  (b) : {b:F3} ms / output token   (~{(b > 0 ? 1000 / b : 0):F1} tok/s)");
    Console.WriteLine($"Sabit   (c) : {pureOverhead:F0} ms   (B kolu kesişimi: {bIntercept:F0} ms)");
    Console.WriteLine();
    Console.WriteLine($">>> 1 output token ≈ {ratio:F0} input token maliyeti <<<");
    Console.WriteLine();
    Console.WriteLine("Sonuç: prompt uzunluğu ucuz, cevap uzunluğu pahalı.");
    Console.WriteLine("RAG'de top-k'yı cömert tut; 'kısa cevap ver' bir performans kararıdır.");

    Csv.Write(config, "02_prefill_decode",
        ["run_id", "model", "kol", "input_tokens", "output_tokens",
         "ttft_any_p50_ms", "total_p50_ms", "total_p95_ms"],
        rows);

    Csv.Write(config, "02_katsayilar",
        ["run_id", "model", "prefill_ms_per_in_token", "decode_ms_per_out_token",
         "tok_per_sec", "sabit_overhead_ms", "out_in_maliyet_orani"],
        [[runId, client.ModelId, Inv(a, "F5"), Inv(b, "F4"),
          Inv(b > 0 ? 1000 / b : 0, "F2"), Inv(pureOverhead, "F1"), Inv(ratio, "F1")]]);
}


// ============================================================================
// DENEY 3 — Determinizm
//
// Gemini'de 10/10 farklı çıktı almıştın. Bundan "LLM non-deterministiktir"
// sonucu çıkarmak YANLIŞ olurdu. Model ağırlıkları sabit bir fonksiyondur;
// non-determinism iki yerden gelir:
//   1. sampling (temperature > 0)
//   2. sunucu tarafı batching / GPU floating-point toplama sırası
//
// v1 hatası: tüm çıktılar boş string'di; "1/20 benzersiz" sonucu 20 adet boş
// string'in eşitliğiydi. v2 reasoning kanalını da okuyor ve boş çıktıyı açıkça
// hata olarak raporluyor.
//
// temp=1.8 kolu bir KONTROL kolu: parametrenin modele ulaştığını doğruluyor.
// Orada bile çeşitlilik yoksa temperature uygulanmıyor demektir.
// ============================================================================
static async Task Experiment3_Determinism(LmClient client, Config config, string runId)
{
    Header("DENEY 3 — Determinizm (4 kol)");

    const int n = 20;
    const string prompt =
        "Bir hakediş raporunda mutlaka bulunması gereken üç unsuru say. Kısa yaz.";

    var arms = new (string Name, double Temp, int? Seed)[]
    {
        ("temp0_seed_sabit", 0.0, 42),
        ("temp0_seed_yok",   0.0, null),
        ("temp08_seed_yok",  0.8, null),
        ("temp18_seed_yok",  1.8, null),   // kontrol kolu
    };

    var rows = new List<string[]>();
    Console.WriteLine($"{"kol",-20} {"benzersiz",12} {"ilk_ile_ayni",14} {"ort_kar",10} {"bos",6}");
    Console.WriteLine(new string('-', 66));

    foreach (var (name, temp, seed) in arms)
    {
        var outputs = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var r = await client.ChatAsync([new Message("user", prompt)],
    2500, temp, seed: seed, disableThinking: true);
            outputs.Add(r.Text.Trim());
        }

        var emptyCount = outputs.Count(string.IsNullOrEmpty);
        var distinct = outputs.Distinct().Count();
        var samePct = 100.0 * outputs.Count(o => o == outputs[0]) / n;
        var avgLen = outputs.Average(o => o.Length);

        Console.WriteLine($"{name,-20} {distinct + "/" + n,12} {samePct,13:F0}% {avgLen,10:F0} {emptyCount,6}");

        if (emptyCount > 0)
            Console.WriteLine($"  UYARI: {emptyCount}/{n} çıktı BOŞ — reasoning kanalını kontrol et.");

        rows.Add([runId, client.ModelId, name,
                  Inv(temp, "F1"), seed?.ToString() ?? "",
                  n.ToString(), distinct.ToString(),
                  Inv(samePct, "F1"), Inv(avgLen, "F0"), emptyCount.ToString()]);

        File.WriteAllText(
            Path.Combine(config.OutputDir, $"03_ornek_{name}.txt"),
            string.Join("\n\n----- yeni çıktı -----\n\n", outputs.Take(3)),
            Encoding.UTF8);
    }

    Console.WriteLine(new string('-', 66));
    Console.WriteLine("Beklenti: temp=0 -> 1/20 benzersiz (deterministik).");
    Console.WriteLine("          temp=1.8 -> yüksek çeşitlilik. DEĞİLSE temperature modele");
    Console.WriteLine("          ulaşmıyor (LM Studio 'Default Parameters' eziyor olabilir).");

    Csv.Write(config, "03_determinizm",
        ["run_id", "model", "kol", "temperature", "seed", "n",
         "benzersiz_cikti", "ilk_ile_ayni_yuzde", "ort_karakter", "bos_cikti_sayisi"],
        rows);
}


// ============================================================================
// DENEY 4 — Statelessness + KV cache (uzun context)
//
// İki ayrı gerçek var, karıştırma:
//
// (a) Protokol seviyesinde: her tur tüm geçmişi yeniden gönderirsin.
//     Token maliyeti kuadratik birikir. Bu değişmez.
//
// (b) Sunucu seviyesinde: aynı prefix'le devam eden istekte prefill yeniden
//     HESAPLANMAZ, KV cache'ten gelir.
//
// v1 hatası: prompt'lar 42-162 token'dı. Prefill katsayısı ~0.19 ms olduğuna
// göre cache'in kurtarabileceği maksimum süre ~30 ms — 1400 ms'lik toplamın
// içinde görünmez. Deney gürültü ölçtü.
//
// v2: system mesajına uzun bir doküman koyuyoruz. Artık prefill baskın
// rejimdeyiz ve cache etkisi sinyal veriyor.
//
// Mimari sonuç: sabit içerik (system prompt + sözleşme metni) BAŞTA, değişken
// içerik (kullanıcı sorusu) SONDA. Sıralamayı bozmak performans hatasıdır.
// ============================================================================
static async Task Experiment4_KvCache(LmClient client, Config config, string runId)
{
    Header($"DENEY 4 — KV cache (uzun context: ~{config.DocWords} kelime)");

    var document = Filler(config.DocWords);

    var turns = new[]
    {
        "Bu dokümana göre hakediş nedir? Kısa açıkla.",
        "Geçici hakediş ile kesin hakediş farkı nedir?",
        "Fiyat farkı bu hesaba nasıl giriyor?",
        "İş artışı olursa ne değişir?",
        "Bunları üç maddede özetle.",
    };

    var rows = new List<string[]>();
    var systemBase = "Aşağıdaki sözleşme dokümanına göre cevap ver.\n\n" + document;

    foreach (var cacheFriendly in new[] { true, false })
    {
        var mode = cacheFriendly ? "cache_dostu" : "cache_dusmani";
        Console.WriteLine($"\n{mode}:");
        Console.WriteLine($"{"tur",4} {"prompt_tok",12} {"kumulatif",12} {"ttftAny",10} {"total",10}");
        Console.WriteLine(new string('-', 52));

        var history = new List<Message> { new("system", systemBase) };
        var cumulative = 0;

        for (var t = 0; t < turns.Length; t++)
        {
            // Cache düşmanı kol: her turda system mesajının BAŞINA benzersiz bir
            // değer koyuyoruz. Tek karakter bile değişse prefix eşleşmesi bozulur
            // ve tüm prefill baştan hesaplanır. Ölçtüğümüz fark tam olarak bu.
            history[0] = cacheFriendly
                ? new Message("system", systemBase)
                : new Message("system", $"[{Guid.NewGuid():N}]\n" + systemBase);

            history.Add(new Message("user", turns[t]));

            var r = await client.ChatStreamAsync(history, 150, 0, disableThinking: true);
            history.Add(new Message("assistant", string.IsNullOrEmpty(r.Text) ? "(bos)" : r.Text));

            cumulative += r.PromptTokens;

            Console.WriteLine($"{t + 1,4} {r.PromptTokens,12} {cumulative,12} {r.TtftAnyMs,10:F1} {r.TotalMs,10:F1}");

            rows.Add([runId, client.ModelId, mode, (t + 1).ToString(),
                      r.PromptTokens.ToString(), cumulative.ToString(),
                      r.CompletionTokens.ToString(),
                      Inv(r.TtftAnyMs, "F2"), Inv(r.TotalMs, "F2")]);
        }
    }

    // Tur 1 hariç: ilk turda iki kol da soğuk, karşılaştırma anlamsız.
    var dostu = rows.Where(r => r[2] == "cache_dostu").Skip(1)
                    .Average(r => double.Parse(r[7], CultureInfo.InvariantCulture));
    var dusman = rows.Where(r => r[2] == "cache_dusmani").Skip(1)
                     .Average(r => double.Parse(r[7], CultureInfo.InvariantCulture));

    Console.WriteLine(new string('-', 52));
    Console.WriteLine($"Ortalama ttftAny (tur 2-5): cache_dostu {dostu:F0} ms | cache_dusmani {dusman:F0} ms");
    Console.WriteLine($"Cache kazancı: {dusman - dostu:F0} ms  (%{100 * (dusman - dostu) / Math.Max(dusman, 1):F0})");
    Console.WriteLine();
    Console.WriteLine("prompt_tokens iki kolda da benzer artar (protokol gerçeği değişmez),");
    Console.WriteLine("ama ttft cache_dostu kolda düşük kalır (sunucu optimizasyonu).");

    Csv.Write(config, "04_kv_cache",
        ["run_id", "model", "mod", "tur", "prompt_tokens", "kumulatif_tokens",
         "completion_tokens", "ttft_any_ms", "total_ms"],
        rows);
}


// ============================================================================
// DENEY 5 — Eşzamanlılık ve throughput
//
// Gemini'de sınır rate limit'ti (burst 429 vs daily quota 429). Lokalde 429 yok;
// sınır GPU'nun kendisi. Ölçtüğümüz şey değişti:
//
//   - toplam throughput (tok/s): bir yere kadar ARTAR, sonra doyar
//   - istek başına latency: BAŞTAN İTİBAREN bozulur
//
// İkisi aynı anda doğrudur. Klasik throughput-latency trade-off'u.
//
// Neden önemli: "tek GPU'lu lokal kurulum kaç eşzamanlı kullanıcı kaldırır"
// sorusunun cevabı. Kapasite planlaması bu eğriden çıkar.
// ============================================================================
static async Task Experiment5_Concurrency(LmClient client, Config config, string runId)
{
    Header("DENEY 5 — Eşzamanlılık / throughput");

    var levels = new[] { 1, 2, 4, 8, 16, 32 };
    var rows = new List<string[]>();

    Console.WriteLine($"{"paralel",8} {"toplam tok/s",14} {"p50 lat",10} {"p95 lat",10} {"istek/s",10} {"hata",6}");
    Console.WriteLine(new string('-', 62));

    foreach (var level in levels)
    {
        var sw = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, level).Select(async _ =>
        {
            // Benzersiz prefix: paralel isteklerin birbirinin cache'inden
            // faydalanmasını engelliyoruz. Yoksa yüksek paralellikte sahte bir
            // throughput artışı ölçeriz.
            var prompt = $"[{Guid.NewGuid():N}] Şantiye iş güvenliği önlemlerini açıkla.";
            try
            {
                return await client.ChatStreamAsync(
                    [new Message("user", prompt)], 200, 0.7, disableThinking: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  hata: {Trunc(ex.Message, 120)}");
                return null;
            }
        }).ToArray();

        var all = await Task.WhenAll(tasks);
        sw.Stop();

        var ok = all.Where(r => r is not null).Select(r => r!).ToArray();
        var failed = all.Length - ok.Length;

        if (ok.Length == 0)
        {
            Console.WriteLine($"{level,8} {"-",14} {"-",10} {"-",10} {"-",10} {failed,6}");
            await Task.Delay(3000);
            continue;
        }

        var wallSec = sw.Elapsed.TotalSeconds;
        var totalOut = ok.Sum(r => r.CompletionTokens);
        var aggTps = totalOut / wallSec;
        var p50 = Stats.Median(ok.Select(r => r.TotalMs));
        var p95 = Stats.Percentile(ok.Select(r => r.TotalMs), 95);
        var rps = ok.Length / wallSec;

        Console.WriteLine($"{level,8} {aggTps,14:F1} {p50,10:F0} {p95,10:F0} {rps,10:F2} {failed,6}");

        rows.Add([runId, client.ModelId, level.ToString(), totalOut.ToString(),
                  Inv(wallSec, "F3"), Inv(aggTps, "F2"),
                  Inv(p50, "F1"), Inv(p95, "F1"), Inv(rps, "F3"), failed.ToString()]);

        // GPU'nun toparlanması için ara — termal ve bellek baskısı bir sonraki
        // seviyeye sızmasın. Ölçüm ortamının kendisi de veridir.
        await Task.Delay(3000);
    }

    Console.WriteLine(new string('-', 62));
    Console.WriteLine("Doyum noktası: toplam tok/s'in artmayı bıraktığı paralellik seviyesi.");
    Console.WriteLine("Kapasite kararı bu noktadan ÖNCESİ, p95 latency bütçene göre alınır.");

    Csv.Write(config, "05_esyamanlilik",
        ["run_id", "model", "paralellik", "toplam_output_token", "duvar_saati_sn",
         "toplam_tok_per_sn", "p50_latency_ms", "p95_latency_ms", "istek_per_sn", "hata_sayisi"],
        rows);
}


// ============================================================================
// DENEY 6 — Kalite spot kontrolü
//
// Lokal inference bedava değil; kalite ödüyorsun. Bu deney otomatik SKOR
// üretmiyor — bilinçli olarak. Faz 3-4'te gerçek eval harness'ı kuracaksın;
// burada amaç çıktıları diske dökmek ve elle okumak.
//
// v2: düşünme AÇIK ve KAPALI iki koşu. Reasoning'in kaliteye katkısı ile
// maliyeti (reasoning token'ları output tarafında sayılır, yani en pahalı
// taraf) yan yana görünsün.
// ============================================================================
static async Task Experiment6_QualitySpotCheck(LmClient client, Config config, string runId)
{
    Header("DENEY 6 — Kalite spot kontrolü (elle değerlendirilecek)");

    var prompts = new[]
    {
        "Hakediş raporu ile kesin hesap arasındaki fark nedir?",
        "Fiyat farkı hesaplamasında kullanılan temel endeks nedir ve neden gereklidir?",
        "İş artışı sözleşme bedelinin yüzde kaçına kadar yapılabilir?",
        "Geçici kabul ile kesin kabul arasında hangi süre işler?",
        "Yükleniciye ödenen avansın teminatı nasıl çözülür?",
        "Bir hakediş dosyasında hangi ekler bulunur? Liste halinde yaz.",
        "Aşağıdaki metinden sözleşme bedelini ve tarihini JSON olarak çıkar: " +
        "\"08.03.2026 tarihli sözleşme kapsamında 12.450.000,00 TL bedelle iş üstlenilmiştir.\" " +
        "Sadece JSON döndür, açıklama yazma.",
        "Bilmediğin bir mevzuat maddesi sorulursa ne yaparsın? Kısa cevap ver.",
    };

    var sb = new StringBuilder();
    sb.AppendLine($"# Kalite spot kontrolü — {client.ModelId} — {runId}");
    sb.AppendLine();
    sb.AppendLine("Her cevabı elle değerlendir: [D]oğru / [K]ısmen / [Y]anlış / [H]alüsinasyon");
    sb.AppendLine();
    sb.AppendLine("Not: bu model mevzuat üzerine eğitilmedi. Amaç RAG ÖNCESİ taban çizgisini");
    sb.AppendLine("görmek — hatalar beklenen sonuçtur ve RAG'in neden gerekli olduğunun kanıtıdır.");
    sb.AppendLine();
    sb.AppendLine("Her soru iki kez soruluyor: düşünme AÇIK ve KAPALI. Reasoning token'ları");
    sb.AppendLine("output tarafında sayılır (en pahalı taraf), o yüzden kalite katkısının");
    sb.AppendLine("maliyeti hak edip etmediği ölçülebilir bir sorudur.");
    sb.AppendLine();

    var rows = new List<string[]>();

    for (var i = 0; i < prompts.Length; i++)
    {
        Console.WriteLine($"  [{i + 1}/{prompts.Length}] {Trunc(prompts[i], 50)}");

        sb.AppendLine($"## Soru {i + 1}");
        sb.AppendLine($"**Prompt:** {prompts[i]}");
        sb.AppendLine();

        foreach (var think in new[] { true, false })
        {
            var label = think ? "düşünme AÇIK" : "düşünme KAPALI";
            var r = await client.ChatAsync([new Message("user", prompts[i])],
                3000, 0, seed: 42, disableThinking: !think);

            sb.AppendLine($"### {label}");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(r.Text) ? "_(boş çıktı — hata)_" : r.Text.Trim());
            sb.AppendLine();
            sb.AppendLine($"*{r.PromptTokens} in / {r.CompletionTokens} out " +
                          $"(reasoning: {r.ReasoningTokens}) — {r.TotalMs:F0} ms*");
            sb.AppendLine();
            sb.AppendLine("**Değerlendirme:** _(D/K/Y/H — elle doldur)_");
            sb.AppendLine();

            rows.Add([runId, client.ModelId, (i + 1).ToString(), think ? "acik" : "kapali",
                      r.PromptTokens.ToString(), r.CompletionTokens.ToString(),
                      r.ReasoningTokens.ToString(), Inv(r.TotalMs, "F0"),
                      r.Text.Length.ToString(), ""]);
        }

        sb.AppendLine("---");
        sb.AppendLine();
    }

    var path = Path.Combine(config.OutputDir, "06_kalite_spot_kontrolu.md");
    File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

    Csv.Write(config, "06_kalite_metrik",
        ["run_id", "model", "soru_no", "dusunme", "prompt_tokens", "completion_tokens",
         "reasoning_tokens", "total_ms", "cevap_karakter", "degerlendirme"],
        rows);

    Console.WriteLine($"\nÇıktı: {path}");
    Console.WriteLine("Bu dosyayı elle doldur. Faz 3 eval veri setinin tohumu olacak.");
}


// ============================================================================
// Yardımcılar
// ============================================================================

static void Header(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 62));
    Console.WriteLine(title);
    Console.WriteLine(new string('=', 62));
}

static string Inv(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

static string Fmt(double ttft) => ttft < 0 ? "yok" : $"{ttft:F0}ms";

static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "...";

// Belirli uzunlukta doldurma metni. Türkçe kullanıyoruz ki prefill ölçümü
// gerçek kullanım senaryosunu yansıtsın (Deney 1'deki token cezası dahil).
static string Filler(int words)
{
    var vocab = new[]
    {
        "yüklenici", "idare", "sözleşme", "hakediş", "imalat", "metraj", "keşif",
        "birim", "fiyat", "teminat", "şantiye", "ihale", "teknik", "şartname",
        "kabul", "muayene", "tutanak", "ödeme", "gecikme", "cezai", "şart",
        "endeks", "avans", "revize", "artış", "azalış", "denetim", "rapor",
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
// LM Studio istemcisi (OpenAI-uyumlu /v1)
// ============================================================================

sealed class LmClient(HttpClient http, string baseUrl, string? modelId)
{
    public string ModelId { get; set; } = modelId ?? "";

    /// <summary>
    /// enable_thinking desteklenmiyorsa /no_think sistem mesajı fallback'i.
    /// Ön kontrol sonucuna göre çalışma zamanında set edilir.
    /// </summary>
    public bool UseNoThinkFallback { get; set; }

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
            throw new InvalidOperationException($"{(int)res.StatusCode} — {TruncStatic(raw, 400)}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var text = "";
        var reasoning = "";

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var msg))
        {
            // Reasoning modelleri burada da ayrı alan kullanıyor.
            // v1 sadece content'e bakıyordu ve boş string alıyordu.
            text = ReadStringProp(msg, "content") ?? "";
            reasoning = ReadStringProp(msg, "reasoning_content")
                     ?? ReadStringProp(msg, "reasoning")
                     ?? "";
        }

        var (pt, ct, rt) = ReadUsage(root);

        return new ChatResult(text, reasoning, pt, ct, rt, sw.Elapsed.TotalMilliseconds);
    }

    // ------------------------------------------------------------------------
    // Streaming
    //
    // TTFT'yi ölçmenin tek yolu bu — non-stream çağrıda prefill ile decode tek
    // bir süreye gömülür ve ayrıştırılamaz.
    //
    // Reasoning modellerinde iki ayrı TTFT vardır ve ikisi de anlamlıdır:
    //   TtftAnyMs     : ilk HERHANGİ bir token. Fiziksel prefill'in bittiği an.
    //                   Prefill katsayısı hesabında bu kullanılır.
    //   TtftContentMs : kullanıcıya GÖRÜNEN ilk token. Algılanan gecikme.
    //                   Timeout bütçesi ve UI kararları buna göre verilir.
    //
    // Ayırmazsan model saniyelerce "sessiz" düşünür, sen de sağlıklı isteği
    // timeout sanıp iptal edersin.
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
                $"{(int)res.StatusCode} — {TruncStatic(await res.Content.ReadAsStringAsync(), 400)}");

        using var stream = await res.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;                  // SSE ayırıcı
            if (!line.StartsWith("data: ")) continue;

            var payload = line[6..];
            if (payload == "[DONE]") break;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(payload); }
            catch (JsonException) { continue; }              // bozuk chunk koşuyu düşürmesin

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var err))
                    throw new InvalidOperationException($"stream error — {err}");

                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("delta", out var delta)
                    && delta.ValueKind == JsonValueKind.Object)
                {
                    // --- reasoning kanalı ---
                    // Farklı sunucular farklı isim kullanıyor; ikisini de tara.
                    var reasoningChunk = ReadStringProp(delta, "reasoning_content")
                                      ?? ReadStringProp(delta, "reasoning");

                    if (!string.IsNullOrEmpty(reasoningChunk))
                    {
                        if (ttftAny < 0) ttftAny = sw.Elapsed.TotalMilliseconds;
                        reasoningSb.Append(reasoningChunk);
                    }

                    // --- görünür içerik kanalı ---
                    var contentChunk = ReadStringProp(delta, "content");
                    if (!string.IsNullOrEmpty(contentChunk))
                    {
                        if (ttftAny < 0) ttftAny = sw.Elapsed.TotalMilliseconds;
                        if (ttftContent < 0) ttftContent = sw.Elapsed.TotalMilliseconds;
                        contentSb.Append(contentChunk);
                    }
                }

                // usage genelde son chunk'ta gelir; her chunk'ta okuyup üzerine yazıyoruz.
                var (p, c, r) = ReadUsage(root);
                if (p > 0) pt = p;
                if (c > 0) ct = c;
                if (r > 0) rt = r;
            }
        }

        sw.Stop();
        var totalMs = sw.Elapsed.TotalMilliseconds;

        if (ttftAny < 0) ttftAny = totalMs;

        // ttftContent = -1 bırakılıyor: hiç görünür içerik üretilmedi demek.
        // 0 yazmak "anında geldi" gibi okunur ve CSV'yi sessizce yalanlar.
        // Eksik veriyi eksik olarak kaydet.

        if (ct == 0)
            ct = Math.Max(1, (contentSb.Length + reasoningSb.Length) / 3);

        return new StreamResult(
            contentSb.ToString(), reasoningSb.ToString(),
            pt, ct, rt, ttftAny, ttftContent, totalMs);
    }

    // ------------------------------------------------------------------------

    Dictionary<string, object> BuildBody(
        List<Message> messages, int maxTokens, double temperature,
        int? seed, bool stream, bool disableThinking)
    {
        var effective = messages;

        // Fallback: chat_template_kwargs desteklenmiyorsa Qwen'de /no_think
        // sistem mesajı da düşünmeyi kapatıyor.
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

        // Ölçüm koşularında düşünmeyi kapat: fiziği ölçüyoruz, akıl yürütmeyi değil.
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

        static int ReadInt(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    }

    static string TruncStatic(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}


// ============================================================================
// Kayıt tipleri
// ============================================================================

record Message(string Role, string Content);

record ChatResult(
    string Text,
    string Reasoning,
    int PromptTokens,
    int CompletionTokens,
    int ReasoningTokens,
    double TotalMs);

record StreamResult(
    string Text,
    string Reasoning,
    int PromptTokens,
    int CompletionTokens,
    int ReasoningTokens,
    double TtftAnyMs,
    double TtftContentMs,   // -1 = hiç görünür içerik üretilmedi
    double TotalMs);


// ============================================================================
// İstatistik
//
// Medyan kullanıyoruz, ortalama değil: GPU'da Windows masaüstü kompozisyonu
// veya termal throttle arada spike yaratır ve ortalamayı bozar. p95'i ayrıca
// raporluyoruz çünkü kuyruk latency'si kapasite kararının asıl girdisi.
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

    /// <summary>En küçük kareler doğrusal uyum. Dönüş: (eğim, kesişim).</summary>
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
}


// ============================================================================
// CSV
// ============================================================================

static class Csv
{
    public static void Write(Config config, string name, string[] header, List<string[]> rows)
    {
        var path = Path.Combine(config.OutputDir, $"{name}.csv");
        var sb = new StringBuilder();

        sb.AppendLine(string.Join(",", header.Select(Escape)));
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Escape)));

        // BOM: Excel Türkçe karakterleri doğru okusun.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"-> {path}");
    }

    static string Escape(string v)
        => v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
}


// ============================================================================
// Konfigürasyon
// ============================================================================

sealed class Config
{
    public string BaseUrl { get; init; } = "http://localhost:1234/v1";
    public string? ModelId { get; init; }
    public string OutputDir { get; init; } = "results";
    public int DocWords { get; init; } = 2000;
    public HashSet<int> Experiments { get; init; } = [1, 2, 3, 4, 5, 6];

    public static Config Parse(string[] args)
    {
        var url = "http://localhost:1234/v1";
        string? model = null;
        var outDir = "results";
        var docWords = 2000;
        var exps = new HashSet<int>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url" when i + 1 < args.Length:
                    url = args[++i];
                    break;
                case "--model" when i + 1 < args.Length:
                    model = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                case "--doc-words" when i + 1 < args.Length:
                    if (int.TryParse(args[i + 1], out var dw)) docWords = dw;
                    i++;
                    break;
                default:
                    if (int.TryParse(args[i], out var n) && n is >= 1 and <= 6) exps.Add(n);
                    break;
            }
        }

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