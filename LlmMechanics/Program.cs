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
//   3. Düşünmeyi kapatmak için üç yol DENENİYOR ve sonucu ön kontrolde
//      raporlanıyor: chat_template_kwargs.enable_thinking, /no_think sistem
//      mesajı, ve (LM Studio tarafında) Reasoning Budget. qwen3.5-9b üzerinde
//      ÜÇÜ DE ETKİSİZ — kod bunu tespit edip ölçümleri düşünme açık koşuyor.
//   4. Deney 2 A kolu max_tokens 16 -> 64 (marjsız değildi)
//   5. Deney 4 uzun context'e taşındı (kısa prompt'ta KV cache etkisi gürültüdeydi)
//   6. Deney 5'e 16 paralellik seviyesi eklendi (eğri doymamıştı)
//   7. Deney 6 düşünme kapatılabiliyorsa iki kol, kapatılamıyorsa tek kol koşuyor
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
    Console.Error.WriteLine($"HATA: çıktı klasörü oluşturulamadı ({config.OutputDir}) — {ex.Message}");
    return 1;
}

Console.WriteLine($"Base URL : {config.BaseUrl}");
Console.WriteLine($"Model    : {(string.IsNullOrWhiteSpace(config.ModelId) ? "(otomatik keşif)" : config.ModelId)}");

if (string.IsNullOrWhiteSpace(config.ModelId))
{
    List<string> models;
    try
    {
        models = await client.ListModelsAsync();
    }
    catch (Exception ex)
    {
        // Buraya gelen istisna eskiden yakalanmıyordu: LM Studio kapalıyken
        // program yığın iziyle düşüyor, aşağıdaki yardımcı mesaj hiç görünmüyordu.
        Console.Error.WriteLine($"HATA: model listesi alınamadı — {ex.Message}");
        Console.Error.WriteLine($"      Sunucu adresi: {config.BaseUrl}");
        Console.Error.WriteLine("      LM Studio > Developer > Status: Running ve model yüklü olmalı.");
        return 1;
    }

    if (models.Count == 0)
    {
        Console.Error.WriteLine("HATA: Sunucuda model yok. LM Studio > Developer > Status: Running ve model yüklü olmalı.");
        return 1;
    }

    Console.WriteLine("\nBulunan modeller:");
    foreach (var m in models) Console.WriteLine($"  - {m}");

    // Yalnızca 'embed' elemek yetmiyordu; rerank/whisper gibi chat olmayan
    // modeller filtreyi geçip sessizce seçilebiliyordu.
    string[] notChat = ["embed", "rerank", "whisper", "clip", "vae", "tts"];

    var chatCandidates = models
        .Where(m => !notChat.Any(t => m.Contains(t, StringComparison.OrdinalIgnoreCase)))
        .ToList();

    if (chatCandidates.Count == 0)
    {
        Console.Error.WriteLine("HATA: Chat modeli bulunamadı (hepsi embedding/rerank görünüyor).");
        Console.Error.WriteLine("      --model ile açıkça belirt.");
        return 1;
    }

    if (chatCandidates.Count > 1)
        Console.WriteLine($"UYARI: {chatCandidates.Count} chat adayı var, ilki seçiliyor. " +
                          "Koşular arası tutarlılık için --model kullan.");

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

// Warm-up ÖN KONTROLDEN ÖNCE: ilk çağrı model yükleme + CUDA context init +
// kernel derlemesi maliyetini üstleniyor. Ön kontrol önce koşarsa bastığı
// ttftAny bu soğuk maliyeti gösterir (ölçülen fark 2413 ms'e karşı 65 ms) ve
// kullanıcı yanlış bir taban çizgisi okur.
Console.WriteLine("\nWarm-up (3 çağrı, ölçüme dahil değil)...");
try
{
    for (var i = 0; i < 3; i++)
        await client.ChatAsync([new Message("user", "Merhaba.")], 16, 0, disableThinking: true);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\nHATA: warm-up çağrısı başarısız — {ex.Message}");
    Console.Error.WriteLine($"      Sunucuya ulaşılamıyor olabilir: {config.BaseUrl}");
    Console.Error.WriteLine("      LM Studio > Developer > Status: Running ve model yüklü mü?");
    return 1;
}
Console.WriteLine("Warm-up tamam.");

Header("ÖN KONTROL — reasoning kanalı ve enable_thinking");

const string probePrompt = "Hakediş nedir? Tek cümle.";

StreamResult probeOn, probeOff;
try
{
    probeOn = await client.ChatStreamAsync(
        [new Message("user", probePrompt)], maxTokens: 300, temperature: 0);

    Console.WriteLine($"Düşünme AÇIK  : content={probeOn.Text.Length} kar, " +
                      $"reasoning={probeOn.Reasoning.Length} kar, " +
                      $"reasoning_tokens={probeOn.ReasoningTokens}, " +
                      $"ttftAny={Fmt(probeOn.TtftAnyMs)}, ttftContent={Fmt(probeOn.TtftContentMs)}");

    probeOff = await client.ChatStreamAsync(
        [new Message("user", probePrompt)], maxTokens: 300, temperature: 0,
        disableThinking: true);

    Console.WriteLine($"Düşünme KAPALI: content={probeOff.Text.Length} kar, " +
                      $"reasoning={probeOff.Reasoning.Length} kar, " +
                      $"reasoning_tokens={probeOff.ReasoningTokens}, " +
                      $"ttftAny={Fmt(probeOff.TtftAnyMs)}, ttftContent={Fmt(probeOff.TtftContentMs)}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\nHATA: ön kontrol çağrısı başarısız — {ex.Message}");
    Console.Error.WriteLine($"      Sunucuya ulaşılamıyor olabilir: {config.BaseUrl}");
    Console.Error.WriteLine("      LM Studio > Developer > Status: Running ve model yüklü mü?");
    return 1;
}

// Bazı sunucular reasoning'i ayrı alanda değil, content içinde <think>...</think>
// olarak yayınlıyor. Bunu kontrol etmezsek "düşünme kapandı" diye yanlış karar
// verir, ttftAny'yi ilk content token'ına damgalar ve tüm katsayıları kirletiriz.
var inlineThink = probeOff.Text.Contains("<think", StringComparison.OrdinalIgnoreCase);

if (inlineThink)
    Console.WriteLine("UYARI: content içinde <think> bloğu görüldü — reasoning ayrı kanalda gelmiyor.");

var thinkingOff = probeOff.Reasoning.Length == 0 && probeOff.Text.Length > 0 && !inlineThink;
client.CanDisableThinking = thinkingOff;

if (!thinkingOff)
{
    Console.WriteLine();
    Console.WriteLine("UYARI: enable_thinking=false etkisiz. /no_think fallback'i deneniyor...");

    // DİKKAT: burası bir zamanlar `= false` yazıyordu; property'nin varsayılanı da
    // false olduğu için fallback hiç devreye girmiyor, aşağıdaki probe da birebir
    // aynı isteği tekrarlıyordu — yani "fallback işe yaramadı" sonucu denenmemiş
    // bir yoldan çıkarılmış oluyordu.
    client.UseNoThinkFallback = true;

    var probeOff2 = await client.ChatStreamAsync(
        [new Message("user", probePrompt)], maxTokens: 300, temperature: 0,
        disableThinking: true);

    Console.WriteLine($"Fallback ile  : content={probeOff2.Text.Length} kar, " +
                      $"reasoning={probeOff2.Reasoning.Length} kar");

    var fallbackWorks = probeOff2.Reasoning.Length == 0 && probeOff2.Text.Length > 0
                        && !probeOff2.Text.Contains("<think", StringComparison.OrdinalIgnoreCase);

    client.CanDisableThinking = fallbackWorks;

    if (fallbackWorks)
    {
        Console.WriteLine("       /no_think fallback'i ÇALIŞIYOR, ölçümlerde kullanılacak.");
        Console.WriteLine("       Not: bu her prompt'a sabit bir system turu ekler ve Deney 1'in");
        Console.WriteLine("       token sayılarını iki tarafta da şişirir (oranı 1'e yaklaştırır).");
    }
    else
    {
        // Geri al. Açık bırakılırsa hiçbir fayda sağlamadan bütün prompt'lara
        // sabit bir system turu ekler — Deney 1'in TR/EN oranını bozan tam da bu.
        client.UseNoThinkFallback = false;
        Console.WriteLine("       Fallback da işe yaramadı, geri alındı. Ölçümler düşünme AÇIK koşacak.");
        Console.WriteLine("       README'ye bu notu düş — katsayılar reasoning dahil demektir.");
    }
}

if (probeOn.Text.Length == 0 && probeOn.Reasoning.Length == 0)
{
    Console.Error.WriteLine("\nHATA: Model hiç içerik üretmedi. Model yüklü mü, context ayarı doğru mu?");
    return 1;
}

// ----------------------------------------------------------------------------
// Not: warm-up yukarıda, ön kontrolden ÖNCE koşuyor — ilk çağrılar model
// yükleme, CUDA context init ve kernel derlemesini içerir. Gemini'de bu maliyet
// yoktu (sunucu zaten sıcaktı); lokalde en büyük outlier kaynağı bu.
// ----------------------------------------------------------------------------

// Her deney kendi try/catch'inde koşuyor: Deney 2'de 50, Deney 3'te 80 sıralı
// çağrı var; birinde 500 alınca tüm koşunun çöpe gitmesi ve sonraki deneylerin
// hiç koşmaması ölçüm saatlerini yakıyordu.
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
        Console.Error.WriteLine($"\nHATA: Deney {no} yarıda kaldı — {ex.GetType().Name}: {ex.Message}");
        Console.Error.WriteLine("      Bu deneyin CSV'si yazılmadı. Sonraki deneylere devam ediliyor.");
    }
}

Console.WriteLine($"\nTamamlandı. Çıktılar: {Path.GetFullPath(config.OutputDir)}");

if (failedExperiments.Count > 0)
{
    Console.Error.WriteLine($"UYARI: başarısız deneyler: {string.Join(", ", failedExperiments)}");
    return 2;
}

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

    // --- Şablon sabiti kalibrasyonu ---
    // Yorumdaki "~15-20 token" şimdiye kadar ÖLÇÜLMEMİŞ bir varsayımdı. Tek
    // karakterlik bir prompt gönderip prompt_tokens'ı okuyoruz: kalan her şey
    // chat template overhead'i. Bu sabit hem payı hem paydayı şişirdiği için
    // ölçülen oranı 1'e doğru itiyor; ölçmeden "alt sınır" demek yetersizdi.
    var calib = await client.ChatAsync([new Message("user", ".")], 1, 0, disableThinking: true);
    var templateOverhead = Math.Max(0, calib.PromptTokens - 1);

    Console.WriteLine($"Şablon sabiti (ölçülen): {templateOverhead} token " +
                      $"(tek karakterlik prompt = {calib.PromptTokens} prompt_tokens)");
    Console.WriteLine();

    var rows = new List<string[]>();

    // Özet istatistikler CSV'ye yazılmış STRING'lerden sabit indekslerle geri
    // parse ediliyordu; kolon eklendiğinde indeks kaydı ve derleyici uyarmadan
    // yanlış kolonun ortalaması raporlandı. Ölçümleri tipli tutuyoruz.
    var measured = new List<(double Ratio, double NetRatio, int TrTok, int EnTok)>();

    Console.WriteLine($"{"metin",-18} {"TR tok",8} {"EN tok",8} {"oran",8} {"net oran",9} {"TR kar",8} {"EN kar",8}");
    Console.WriteLine(new string('-', 72));

    foreach (var (name, tr, en) in pairs)
    {
        // max_tokens=1 + düşünme kapalı: üretim maliyeti minimum,
        // sadece prompt_tokens'a bakıyoruz.
        var trRes = await client.ChatAsync([new Message("user", tr)], 1, 0, disableThinking: true);
        var enRes = await client.ChatAsync([new Message("user", en)], 1, 0, disableThinking: true);

        if (trRes.PromptTokens == 0 || enRes.PromptTokens == 0)
            throw new InvalidOperationException(
                $"'{name}' için prompt_tokens 0 geldi — sunucu usage alanını göndermiyor, " +
                "bu deneyin tüm oranları anlamsız olurdu.");

        var ratio = (double)trRes.PromptTokens / enRes.PromptTokens;

        // Şablon sabitinden arındırılmış oran: asıl metin-içi ceza bu.
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

    // Üç ayrı toplama, üçü de farklı bir soruya cevap veriyor. Tek sayı raporlamak
    // hangisinin kastedildiğini belirsiz bırakıyordu.
    var avg = measured.Average(m => m.Ratio);                                            // makro
    var max = measured.Max(m => m.Ratio);
    var trTotal = measured.Sum(m => m.TrTok);
    var enTotal = measured.Sum(m => m.EnTok);
    var micro = (double)trTotal / Math.Max(enTotal, 1);                                  // token ağırlıklı
    var netAvg = measured.Average(m => m.NetRatio);                                      // şablondan arındırılmış
    var netMax = measured.Max(m => m.NetRatio);

    Console.WriteLine(new string('-', 72));
    Console.WriteLine($"Ham oran      : ortalama {avg:F3} | en kötü {max:F3} | token-ağırlıklı {micro:F3}");
    Console.WriteLine($"Net oran      : ortalama {netAvg:F3} | en kötü {netMax:F3}   (şablon sabiti çıkarılmış)");
    Console.WriteLine("Gemini referansı: 1.33. Chunk boyutu hesabını ORTALAMAYA değil");
    Console.WriteLine("EN KÖTÜ DURUMA göre yap — terim yoğun listeler en pahalı metin tipi.");
    Console.WriteLine("Chunk planı için NET en kötü durumu kullan: gerçek metin-içi ceza odur.");

    Csv.Write(config, "01_tokenization",
        ["run_id", "model", "metin", "tr_tokens", "en_tokens", "oran",
         "tr_karakter", "en_karakter", "tr_kar_per_token", "en_kar_per_token",
         "sablon_sabiti_token", "tr_net_tokens", "en_net_tokens", "net_oran"],
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

    const double aTemp = 0.0;
    const double bTemp = 0.7;

    // Ölçüm noktası -> satır. Kolonlar 3..7 arası indeksleri aşağıdaki regresyon
    // kullandığı için sabit; yeni kolonlar SONA ekleniyor.
    void AddRow(string arm, double temp, List<StreamResult> samples, double inTok, double outTok)
    {
        // ttftAny ölçülemeyen örnekleri (hiç chunk ayrıştırılamadı) dışarıda
        // bırakıyoruz; totalMs'e düşürüp ortalamaya karıştırmak v1'in ttft==total
        // hatasını sessizce geri getiriyordu.
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

    Console.WriteLine($"A kolu — input DEĞİŞKEN, output sabit ({aOutTokens} token tavanı), temperature={aTemp}:");
    Console.WriteLine($"{"in_tok",8} {"out_tok",8} {"out±",8} {"ttftAny",10} {"total_p50",10} {"total_p95",10}");
    Console.WriteLine(new string('-', 60));

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

    Console.WriteLine($"\nB kolu — input sabit (kısa), output DEĞİŞKEN, temperature={bTemp}:");
    Console.WriteLine($"{"in_tok",8} {"out_tok",8} {"ttftAny",10} {"total_p50",10} {"tok/s",10}");
    Console.WriteLine(new string('-', 50));

    foreach (var size in outputSizes)
    {
        var samples = new List<StreamResult>();
        for (var i = 0; i < reps; i++)
        {
            var prompt = $"[{Guid.NewGuid():N}] Şantiye güvenliği hakkında uzun bir metin yaz.";
            samples.Add(await client.ChatStreamAsync(
                [new Message("user", prompt)], size, bTemp, disableThinking: true));
        }

        var ttftVals = samples.Where(s => s.TtftAnyMs >= 0).Select(s => s.TtftAnyMs).ToList();
        var ttft = ttftVals.Count > 0 ? Stats.Median(ttftVals) : -1;
        var p50 = Stats.Median(samples.Select(s => s.TotalMs));
        var outTok = samples.Average(s => (double)s.CompletionTokens);
        var inTok = samples.Average(s => (double)s.PromptTokens);

        // Bölme koruması: ttft ile total çakışırsa (v1'deki hata) sonsuz yazma.
        var span = ttft >= 0 ? p50 - ttft : -1;
        var tpsText = span > 1.0 ? $"{outTok / (span / 1000.0):F1}" : "n/a";

        Console.WriteLine($"{inTok,8:F0} {outTok,8:F1} {Fmt(ttft),10} {p50,10:F1} {tpsText,10}");

        AddRow("B_decode", bTemp, samples, inTok, outTok);
    }

    // --- Katsayı çıkarımı ---
    // b (decode): B kolunda total'i output_tokens'a regresyon et.
    // a (prefill): A kolunda input_tokens'a regresyon et — İKİ kez.
    //
    //   a_total : total_p50 üzerinden (eski davranış, karşılaştırılabilirlik için)
    //   a_ttft  : ttft_any_p50 üzerinden (temiz ölçüm)
    //
    // İkisi neden farklı: A kolunda max_tokens bir TAVAN, sabit değil. Çıktı
    // uzunluğu input ile birlikte oynarsa decode terimi total'e dayalı eğime
    // sızar (atlanmış-değişken sapması). ttftAny prefill'in bittiği anı damgalar
    // ve decode'dan tamamen bağımsızdır — prefill katsayısının doğru ölçüm aracı
    // budur. Farkın büyük olması çıktı uzunluğunun sabit olmadığını gösterir.

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

    // Prefill katsayısı olarak ttft'ye dayalı olanı kullanıyoruz; ölçülemediyse
    // (hiç ttft yoksa) eskisine düşüyoruz ve bunu açıkça söylüyoruz.
    var aFromTtft = aTtftX.Length >= 2 && aTtft > 0;
    var a = aFromTtft ? aTtft : aTotal;

    var aAvgOut = aRows.Average(r => Col(r, 4));
    var pureOverhead = aTotalIntercept - b * aAvgOut;
    var ratio = a > 1e-9 ? b / a : double.NaN;

    Console.WriteLine(new string('-', 60));
    Console.WriteLine($"Prefill (a) : {a:F4} ms / input token   [{(aFromTtft ? "ttft tabanlı" : "total tabanlı — ttft yok")}]");
    Console.WriteLine($"   a_ttft   : {aTtft:F4}  (R²={aTtftR2:F3}, n={aTtftX.Length})");
    Console.WriteLine($"   a_total  : {aTotal:F4}  (R²={aTotalR2:F3}, n={aX.Length})  <- eski yöntem");
    Console.WriteLine($"Decode  (b) : {b:F3} ms / output token   (~{(b > 0 ? 1000 / b : 0):F1} tok/s, R²={bR2:F3})");
    Console.WriteLine($"Sabit   (c) : {pureOverhead:F0} ms   (B kolu kesişimi: {bIntercept:F0} ms)");
    Console.WriteLine();

    // Sessiz saçmalama koruması. Eskiden a<=0 iken CSV'ye "oran = 0" yazılıyordu
    // ve bu, ölçülmüş bir sonuç gibi okunuyordu.
    var suspect = new List<string>();
    if (a <= 1e-9) suspect.Add($"prefill katsayısı pozitif değil (a={a:F5})");
    if (b <= 1e-9) suspect.Add($"decode katsayısı pozitif değil (b={b:F5})");
    if (pureOverhead < 0) suspect.Add($"sabit overhead negatif ({pureOverhead:F0} ms) — b farklı bir rejimden ödünç alınıyor");
    if (aTtftR2 < 0.9 && aTtftX.Length >= 2) suspect.Add($"prefill uyumu zayıf (R²={aTtftR2:F3})");
    if (bR2 < 0.9) suspect.Add($"decode uyumu zayıf (R²={bR2:F3})");
    if (Math.Abs(aTotal - aTtft) > 0.5 * Math.Max(aTtft, 1e-9) && aFromTtft)
        suspect.Add("a_total ile a_ttft %50'den fazla ayrışıyor — A kolunda çıktı uzunluğu sabit değil");

    if (double.IsNaN(ratio))
        Console.WriteLine(">>> out/in maliyet oranı HESAPLANAMADI (a <= 0) <<<");
    else
        Console.WriteLine($">>> 1 output token ≈ {ratio:F0} input token maliyeti <<<");

    if (suspect.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("UYARI — katsayılar şüpheli, olduğu gibi rapora taşıma:");
        foreach (var s in suspect) Console.WriteLine($"  - {s}");
    }

    Console.WriteLine();
    Console.WriteLine("Sonuç: prompt uzunluğu ucuz, cevap uzunluğu pahalı.");
    Console.WriteLine("RAG'de top-k'yı cömert tut; 'kısa cevap ver' bir performans kararıdır.");

    Csv.Write(config, "02_prefill_decode",
        ["run_id", "model", "kol", "input_tokens", "output_tokens",
         "ttft_any_p50_ms", "total_p50_ms", "total_p95_ms",
         "temperature", "n_ornek", "n_ttft_olculen",
         "out_tok_min", "out_tok_max", "tahmini_out_token_sayisi", "max_tokens_kesilen"],
        rows);

    Csv.Write(config, "02_katsayilar",
        ["run_id", "model", "prefill_ms_per_in_token", "prefill_kaynak",
         "prefill_ttft_ms_per_in_token", "prefill_ttft_r2",
         "prefill_total_ms_per_in_token", "prefill_total_r2",
         "decode_ms_per_out_token", "decode_r2", "tok_per_sec",
         "sabit_overhead_ms", "b_kolu_kesisim_ms", "out_in_maliyet_orani",
         "n_ornek_per_nokta", "a_kolu_temperature", "b_kolu_temperature", "uyarilar"],
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
    Console.WriteLine($"{"kol",-20} {"benzersiz",12} {"bos_haric",11} {"ilk_ile_ayni",13} {"ort_kar",9} {"bos",5}");
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

        // Boş çıktılar da "benzersiz" sayılıyordu: temp=1.8 kolundaki 20/20'nin
        // bir birimi tek bir boş string'di. Boş hariç sayımı ayrı raporluyoruz.
        var distinctNonEmpty = nonEmpty.Distinct().Count();
        var samePct = 100.0 * outputs.Count(o => o == outputs[0]) / n;
        var avgLen = outputs.Average(o => o.Length);
        var avgLenNonEmpty = nonEmpty.Count > 0 ? nonEmpty.Average(o => o.Length) : 0;

        // v1'in hatası reasoning'i hiç okumamaktı; Deney 3 hâlâ yalnızca Text'i
        // saklayıp reasoning'i, token sayılarını ve süreyi atıyordu — yani boş
        // çıktının sebebi (bütçe reasoning'e mi gitti?) veriden görülemiyordu.
        var avgReasoningChars = results.Average(r => (double)r.Reasoning.Length);
        var avgCompletionTok = results.Average(r => (double)r.CompletionTokens);
        var avgReasoningTok = results.Average(r => (double)r.ReasoningTokens);
        var truncated = results.Count(r => r.FinishReason == "length");

        Console.WriteLine($"{name,-20} {distinct + "/" + n,12} {distinctNonEmpty + "/" + nonEmpty.Count,11} " +
                          $"{samePct,12:F0}% {avgLen,9:F0} {emptyCount,5}");

        if (emptyCount > 0)
            Console.WriteLine($"  UYARI: {emptyCount}/{n} çıktı BOŞ " +
                              $"(ort. reasoning {avgReasoningChars:F0} kar / {avgReasoningTok:F0} token — " +
                              "bütçe düşünmeye gitmiş olabilir).");

        if (truncated > 0)
            Console.WriteLine($"  UYARI: {truncated}/{n} çıktı max_tokens tavanında kesildi — " +
                              "çeşitlilik sayımı kesik metinleri karşılaştırıyor.");

        rows.Add([runId, client.ModelId, name,
                  Inv(temp, "F1"), seed?.ToString() ?? "",
                  n.ToString(), distinct.ToString(),
                  Inv(samePct, "F1"), Inv(avgLen, "F0"), emptyCount.ToString(),
                  distinctNonEmpty.ToString(), Inv(avgLenNonEmpty, "F0"),
                  Inv(avgReasoningChars, "F0"), Inv(avgCompletionTok, "F0"),
                  Inv(avgReasoningTok, "F0"), truncated.ToString()]);

        // 20 çıktının 17'si diske hiç ulaşmıyordu; Levenshtein/self-BLEU gibi
        // dereceli çeşitlilik metrikleri sonradan hesaplanamıyordu. Hepsini yaz.
        var dump = new StringBuilder();
        dump.AppendLine($"# {name} — temp={temp}, seed={(seed?.ToString() ?? "yok")}, n={n} — {runId}");
        for (var i = 0; i < results.Count; i++)
        {
            dump.AppendLine();
            dump.AppendLine($"----- çıktı {i + 1}/{n} " +
                            $"(content {outputs[i].Length} kar, reasoning {results[i].Reasoning.Length} kar, " +
                            $"finish={results[i].FinishReason}) -----");
            dump.AppendLine();
            dump.AppendLine(outputs[i].Length == 0 ? "(boş)" : outputs[i]);
        }

        WriteTextOutput(config, $"03_ornek_{name}.txt", dump.ToString(), runId);
    }

    Console.WriteLine(new string('-', 74));
    Console.WriteLine("Beklenti: temp=0 -> 1/20 benzersiz (deterministik).");
    Console.WriteLine("          temp=1.8 -> yüksek çeşitlilik. DEĞİLSE temperature modele");
    Console.WriteLine("          ulaşmıyor (LM Studio 'Default Parameters' eziyor olabilir).");
    Console.WriteLine();
    Console.WriteLine("SINIR: çağrılar sıralı — aynı anda tek istek uçuyor. Sunucu tarafı");
    Console.WriteLine("       batching kaynaklı non-determinizm bu tasarımla ÖLÇÜLEMEZ.");
    Console.WriteLine("SINIR: 'benzersiz' tam string eşitliği; n'de doyar. temp=0.8 ile 1.8'i");
    Console.WriteLine("       ayırt edemez, bu yüzden kontrol kolu zayıf bir sinyaldir.");

    Csv.Write(config, "03_determinizm",
        ["run_id", "model", "kol", "temperature", "seed", "n",
         "benzersiz_cikti", "ilk_ile_ayni_yuzde", "ort_karakter", "bos_cikti_sayisi",
         "benzersiz_bos_haric", "ort_karakter_bos_haric",
         "ort_reasoning_karakter", "ort_completion_token", "ort_reasoning_token",
         "max_tokens_kesilen"],
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
    var emptyAssistantTurns = 0;

    foreach (var cacheFriendly in new[] { true, false })
    {
        var mode = cacheFriendly ? "cache_dostu" : "cache_dusmani";
        Console.WriteLine($"\n{mode}:");
        Console.WriteLine($"{"tur",4} {"prompt_tok",12} {"kumulatif",12} {"cevap_kar",10} {"ttftAny",10} {"total",10}");
        Console.WriteLine(new string('-', 62));

        var history = new List<Message> { new("system", systemBase) };
        var cumulativePrompt = 0;
        var cumulativeTotal = 0;

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

            // Model yalnızca reasoning ürettiyse content boş gelir. Geçmişe
            // reasoning YAZMIYORUZ (model kendi düşüncesini geri okumaz), ama
            // "(bos)" yazıp sessizce geçmek de yanlıştı: CSV, 150 token'lık bir
            // cevap eklenmiş gibi okunuyordu. Gerçeği ayrı bir kolona yazıyoruz.
            var answer = r.Text.Trim();
            if (answer.Length == 0) emptyAssistantTurns++;
            history.Add(new Message("assistant", answer.Length == 0 ? "(cevap yok)" : answer));

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

    // Tur 1 hariç: ilk turda iki kol da soğuk, karşılaştırma anlamsız.
    // Filtre pozisyonel Skip(1) değil, tur kolonu üzerinden: döngü sırası
    // değişirse Skip(1) sessizce yanlış satırı atardı.
    // Ölçülemeyen ttft (-1) ortalamaya girmiyor.
    static double[] Ttfts(List<string[]> src, string mode) => src
        .Where(r => r[2] == mode && r[3] != "1")
        .Select(r => double.Parse(r[7], CultureInfo.InvariantCulture))
        .Where(v => v >= 0)
        .ToArray();

    var dostuVals = Ttfts(rows, "cache_dostu");
    var dusmanVals = Ttfts(rows, "cache_dusmani");

    if (dostuVals.Length == 0 || dusmanVals.Length == 0)
    {
        Console.WriteLine(new string('-', 62));
        Console.WriteLine("UYARI: karşılaştırma için yeterli ttft ölçümü yok, özet atlanıyor.");
    }
    else
    {
        // Medyan — modülün kendi kuralı bu (bkz. Stats bloğu). Burada Average
        // kullanılıyordu ve tek bir termal spike manşet sayıyı bozabiliyordu.
        var dostu = Stats.Median(dostuVals);
        var dusman = Stats.Median(dusmanVals);

        // Ham fark yanıltıcı olabilir: cache_dusmani kolunun prompt'u GUID
        // yüzünden birkaç token daha uzun. Token başına normalize edip de veriyoruz.
        var dostuTok = rows.Where(r => r[2] == "cache_dostu" && r[3] != "1")
                           .Average(r => double.Parse(r[4], CultureInfo.InvariantCulture));
        var dusmanTok = rows.Where(r => r[2] == "cache_dusmani" && r[3] != "1")
                            .Average(r => double.Parse(r[4], CultureInfo.InvariantCulture));

        Console.WriteLine(new string('-', 62));
        Console.WriteLine($"Medyan ttftAny (tur 2-5): cache_dostu {dostu:F0} ms | cache_dusmani {dusman:F0} ms");
        Console.WriteLine($"Cache kazancı: {dusman - dostu:F0} ms  (%{100 * (dusman - dostu) / Math.Max(dusman, 1):F0})");
        Console.WriteLine($"Prompt boyutu (tur 2-5 ort.): dostu {dostuTok:F0} tok | dusmani {dusmanTok:F0} tok");
        Console.WriteLine($"Token başına prefill: dostu {dostu / Math.Max(dostuTok, 1):F4} ms | " +
                          $"dusmani {dusman / Math.Max(dusmanTok, 1):F4} ms/token");
        Console.WriteLine();
        Console.WriteLine("prompt_tokens iki kolda da benzer artar (protokol gerçeği değişmez),");
        Console.WriteLine("ama ttft cache_dostu kolda düşük kalır (sunucu optimizasyonu).");
    }

    if (emptyAssistantTurns > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"UYARI: {emptyAssistantTurns} turda asistan cevabı BOŞ geldi ve geçmişe");
        Console.WriteLine("       '(cevap yok)' olarak eklendi. Yani bu bir 5 turluk GERÇEK konuşma");
        Console.WriteLine("       değil; completion_tokens kolonu cevabın geçmişe girdiğini göstermez.");
        Console.WriteLine("       KV cache karşılaştırması geçerli, konuşma anlatısı değil.");
    }

    Csv.Write(config, "04_kv_cache",
        ["run_id", "model", "mod", "tur", "prompt_tokens", "kumulatif_prompt_tokens",
         "completion_tokens", "ttft_any_ms", "total_ms",
         "kumulatif_toplam_tokens", "asistan_cevap_karakter", "reasoning_karakter", "finish_reason"],
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

    Console.WriteLine($"{"paralel",8} {"basarili",9} {"toplam tok/s",14} {"p50 lat",10} {"p95 lat",10} {"istek/s",10} {"hata",6}");
    Console.WriteLine(new string('-', 72));

    foreach (var level in levels)
    {
        var errors = new List<string>();
        var errorLock = new object();
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
                // Hata TÜRÜ kalıcı veriye geçsin: 429 / timeout / bağlantı kopması
                // / 500 ayrımı yapılamadığında sınırın istemcide mi sunucuda mı
                // olduğu sonradan hiç bilinemiyordu.
                lock (errorLock) errors.Add(ex.GetType().Name);
                Console.WriteLine($"  hata ({ex.GetType().Name}): {Trunc(ex.Message, 120)}");
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
            // Satır YAZILIYOR. Eskiden continue ediliyordu ve CSV'de "tamamen
            // çöktü" ile "hiç denenmedi" ayırt edilemiyordu.
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
            Console.WriteLine($"  UYARI: duvar saati TÜM istekleri (başarısızlar dahil) kapsıyor, " +
                              $"throughput {failed} hata yüzünden düşük görünüyor.");

        rows.Add([runId, client.ModelId, level.ToString(), totalOut.ToString(),
                  Inv(wallSec, "F3"), Inv(aggTps, "F2"),
                  Inv(p50, "F1"), Inv(p95, "F1"), Inv(rps, "F3"), failed.ToString(),
                  ok.Length.ToString(), errorKinds, failed > 0 ? "1" : "0"]);

        // GPU'nun toparlanması için ara — termal ve bellek baskısı bir sonraki
        // seviyeye sızmasın. Ölçüm ortamının kendisi de veridir.
        await Task.Delay(3000);
    }

    Console.WriteLine(new string('-', 72));
    Console.WriteLine("Doyum noktası: toplam tok/s'in artmayı bıraktığı paralellik seviyesi.");
    Console.WriteLine("Kapasite kararı bu noktadan ÖNCESİ, p95 latency bütçene göre alınır.");
    Console.WriteLine();
    Console.WriteLine("SINIR: p95 burada nearest-rank ile hesaplanıyor ve n <= 20 iken");
    Console.WriteLine("       matematiksel olarak MAKSİMUM'a eşittir (n = başarılı istek sayısı).");
    Console.WriteLine("       Yani p50/p95 kuyruk istatistiği değil, tek batch'in en yavaş isteği.");
    Console.WriteLine("SINIR: seviyeler tek koşu ve daima artan sırada — en yüksek paralellik");
    Console.WriteLine("       daima en ısınmış GPU'da ölçülüyor. Tekrar/karşı-dengeleme yok.");
    Console.WriteLine("ÖNCE DOĞRULA: sunucunun Max Concurrent Predictions ayarı test edilen en");
    Console.WriteLine("       yüksek seviyeden BÜYÜK olmalı; değilse ölçtüğün şey GPU doyumu");
    Console.WriteLine("       değil, config kuyruğudur ve 'doyum noktası' dairesel bir sonuç olur.");

    Csv.Write(config, "05_esyamanlilik",
        ["run_id", "model", "paralellik", "toplam_output_token", "duvar_saati_sn",
         "toplam_tok_per_sn", "p50_latency_ms", "p95_latency_ms", "istek_per_sn", "hata_sayisi",
         "basarili_istek", "hata_turleri", "guvenilmez"],
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

    // Düşünme gerçekten kapanmıyorsa iki kol AYNI isteği gönderir ve deney
    // hiçbir şey ölçmez. Bunu baştan söyleyip çıktıya damgalıyoruz — eski
    // koşularda iki kolun token sayıları birebir aynı çıkıyordu ve fark
    // yalnızca dosyayı elle okuyunca görülüyordu.
    if (!client.CanDisableThinking)
    {
        Console.WriteLine("UYARI: düşünme kapatılamıyor (ön kontrol). Tek kol koşulacak;");
        Console.WriteLine("       'acik' / 'kapali' karşılaştırması bu koşuda yapılmayacak.");
        sb.AppendLine("> **UYARI:** Ön kontrolde düşünme kapatılamadı. Aşağıdaki çıktılar tek");
        sb.AppendLine("> kola aittir; açık/kapalı kalite-maliyet karşılaştırması YAPILMAMIŞTIR.");
        sb.AppendLine("> isteği gönderiyor; token sayılarının birebir aynı çıkması beklenen");
        sb.AppendLine("> sonuçtur ve bir kalite/maliyet karşılaştırması DEĞİLDİR.");
        sb.AppendLine();
    }

    var rows = new List<string[]>();

    for (var i = 0; i < prompts.Length; i++)
    {
        Console.WriteLine($"  [{i + 1}/{prompts.Length}] {Trunc(prompts[i], 50)}");

        sb.AppendLine($"## Soru {i + 1}");
        sb.AppendLine($"**Prompt:** {prompts[i]}");
        sb.AppendLine();

        var arms = client.CanDisableThinking ? new[] { true, false } : new[] { true };

        foreach (var think in arms)
        {
            var label = think ? "düşünme AÇIK" : "düşünme KAPALI";
            var r = await client.ChatAsync([new Message("user", prompts[i])],
                3000, 0, seed: 42, disableThinking: !think);

            // max_tokens reasoning + content TOPLAMINI kapsıyor. Bütçe düşünmeye
            // gidip content'e hiç sıra gelmediğinde bu bir "hata" değil, bir
            // BÜTÇE olayıdır; ikisini aynı etikete koymak yanlış teşhis üretiyordu.
            var truncated = r.FinishReason == "length";
            var body = r.Text.Trim();

            sb.AppendLine($"### {label}");
            sb.AppendLine();

            if (body.Length > 0)
            {
                sb.AppendLine(body);
                if (truncated)
                    sb.AppendLine("\n_(max_tokens tavanında kesildi — cevap eksik)_");
            }
            else if (truncated)
            {
                sb.AppendLine($"_(görünür cevap YOK — {r.ReasoningTokens} token'lık bütçenin tamamı " +
                              "düşünmeye gitti ve max_tokens tavanına dayandı; bu bir hata değil, " +
                              "bütçe olayıdır)_");
            }
            else
            {
                sb.AppendLine("_(boş çıktı — hata)_");
            }

            sb.AppendLine();
            sb.AppendLine($"*{r.PromptTokens} in / {r.CompletionTokens} out " +
                          $"(reasoning: {r.ReasoningTokens}) — {r.TotalMs:F0} ms — finish: {r.FinishReason}*");
            sb.AppendLine();

            // Reasoning metni hiç diske yazılmıyordu; "1016 token düşünüp yanlış
            // cevap verdi" gibi bir iddia çıktı dosyalarından doğrulanamıyordu.
            if (r.Reasoning.Length > 0)
            {
                sb.AppendLine("<details><summary>reasoning (" + r.Reasoning.Length + " karakter)</summary>");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(r.Reasoning.Trim());
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }

            sb.AppendLine("**Değerlendirme:** _(D/K/Y/H — elle doldur)_");
            sb.AppendLine();

            rows.Add([runId, client.ModelId, (i + 1).ToString(), think ? "acik" : "kapali",
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

    // Elle doldurulan değerlendirmeleri EZMİYORUZ. Eskiden aynı ada
    // File.WriteAllText yapılıyordu ve ikinci koşu saatlerce süren insan
    // emeğini uyarısız siliyordu.
    var path = WriteTextOutput(config, "06_kalite_spot_kontrolu.md", sb.ToString(), runId);

    Csv.Write(config, "06_kalite_metrik",
        ["run_id", "model", "soru_no", "dusunme", "prompt_tokens", "completion_tokens",
         "reasoning_tokens", "total_ms", "cevap_karakter", "degerlendirme",
         "reasoning_karakter", "finish_reason", "max_tokens_kesilen", "dusunme_kapatilabildi"],
        rows);

    Console.WriteLine($"\nÇıktı: {path}");
    Console.WriteLine("Bu dosyayı elle doldur. Faz 3 eval veri setinin tohumu olacak.");

    if (client.CanDisableThinking)
    {
        var identical = rows.Where((_, idx) => idx % 2 == 0)
                            .Zip(rows.Where((_, idx) => idx % 2 == 1),
                                 (on, off) => on[5] == off[5] && on[6] == off[6] && on[8] == off[8])
                            .Count(same => same);

        if (identical > 0)
            Console.WriteLine($"UYARI: {identical}/{prompts.Length} soruda iki kol birebir aynı " +
                              "sonucu verdi — düşünme kapandığı sanılıyor ama kapanmıyor.");
    }
    else
    {
        Console.WriteLine("NOT: düşünme kapatılamadığı için tek kol koşuldu; " +
                          "açık/kapalı karşılaştırması bu koşuda YAPILMADI.");
    }
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

static string Trunc(string s, int max) => Text.Trunc(s, max);

/// <summary>
/// Metin çıktısını yazar ama VAR OLANI EZMEZ. Deney 3'ün örnekleri ve Deney 6'nın
/// Markdown'ı elle doldurulan alanlar içeriyor; sabit ada File.WriteAllText yapmak
/// ikinci koşuda insan emeğini uyarısız siliyordu. Dosya varsa run_id'li bir
/// kardeş dosyaya yazıp yolu döndürüyoruz.
/// </summary>
static string WriteTextOutput(Config config, string fileName, string content, string runId)
{
    var path = Path.Combine(config.OutputDir, fileName);

    if (File.Exists(path))
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        path = Path.Combine(config.OutputDir, $"{stem}.{runId}{ext}");
        Console.WriteLine($"   NOT: {fileName} zaten var (elle doldurulmuş olabilir), " +
                          $"yeni çıktı {Path.GetFileName(path)} olarak yazıldı.");
    }

    File.WriteAllText(path, content, Encoding.UTF8);
    return path;
}

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

    /// <summary>
    /// Ön kontrolde düşünmenin gerçekten kapatılabildiği doğrulandı mı?
    /// Deney 6 buna bakıp "iki kol aynı isteği gönderiyor" uyarısını basıyor.
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

        // Bazı OpenAI-uyumlu sunucular hatayı 200 ile { "error": ... } gövdesinde
        // döndürüyor. Stream yolunda bu kontrol vardı, burada yoktu: sonuç boş bir
        // ChatResult olarak sessizce geçip Deney 1'de "oran 0.000" gibi masum
        // görünen ama yanlış sayılara dönüşüyordu.
        if (root.TryGetProperty("error", out var errEl))
            throw new InvalidOperationException($"sunucu hatası (HTTP 200) — {Text.Trunc(errEl.ToString(), 400)}");

        var text = "";
        var reasoning = "";
        var finishReason = "";

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            if (choices[0].TryGetProperty("message", out var msg))
            {
                // Reasoning modelleri burada da ayrı alan kullanıyor.
                // v1 sadece content'e bakıyordu ve boş string alıyordu.
                text = ReadStringProp(msg, "content") ?? "";
                reasoning = ReadStringProp(msg, "reasoning_content")
                         ?? ReadStringProp(msg, "reasoning")
                         ?? "";
            }

            // finish_reason hiç okunmuyordu: max_tokens tavanında kesilmiş bir
            // cevap ile normal biten cevap ayırt edilemiyor, budanma "hata" diye
            // etiketleniyordu.
            finishReason = ReadStringProp(choices[0], "finish_reason") ?? "";
        }

        var (pt, ct, rt) = ReadUsage(root);

        return new ChatResult(text, reasoning, pt, ct, rt, sw.Elapsed.TotalMilliseconds, finishReason);
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
                $"{(int)res.StatusCode} — {Text.Trunc(await res.Content.ReadAsStringAsync(), 400)}");

        using var stream = await res.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var finishReason = "";
        var dataLines = 0;

        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;                  // SSE ayırıcı
            if (!line.StartsWith("data:")) continue;         // ':' sonrası boşluk SSE'de OPSİYONEL

            // Eskiden "data: " (boşluklu) sabit prefix'i aranıyor ve line[6..]
            // ile kesiliyordu; "data:{...}" yayan bir sunucuda TÜM chunk'lar
            // sessizce düşüyor, ttft ölçülemiyor ve v1'in ttft==total hatası
            // hiçbir uyarı vermeden geri geliyordu.
            var payload = line[5..].TrimStart();
            if (payload.Length == 0) continue;
            if (payload.AsSpan().Trim().SequenceEqual("[DONE]")) break;

            dataLines++;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(payload); }
            catch (JsonException) { continue; }              // bozuk chunk koşuyu düşürmesin

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

                    var fr = ReadStringProp(choices[0], "finish_reason");
                    if (!string.IsNullOrEmpty(fr)) finishReason = fr;
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

        // ttftAny = -1 KORUNUYOR. Eskiden totalMs'e düşürülüyordu; bu, dosyanın
        // başında v1'in ana hatası olarak tarif edilen ttft == total durumunu
        // bayraksız üretiyor ve regresyona gerçek ölçüm gibi giriyordu.
        // ttftContent = -1 de aynı sebeple bırakılıyor: hiç görünür içerik
        // üretilmedi demek. 0 yazmak "anında geldi" gibi okunur ve CSV'yi
        // sessizce yalanlar. Eksik veriyi eksik olarak kaydet.

        if (dataLines == 0)
            throw new InvalidOperationException(
                "stream'den hiç 'data:' satırı okunamadı — SSE formatı beklenenden farklı.");

        // Sunucu usage göndermediyse token sayısını TAHMİN ediyoruz, ama bunu
        // bayrakla işaretliyoruz: tahmin ile ölçüm CSV'de ayırt edilebilsin.
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

        // GetInt32 korumasızdı: usage alanı "128.0" gibi ondalıklı ya da
        // int.MaxValue üstü gelirse FormatException atıyor ve bu istisna
        // aşağıdaki JsonException catch'i tarafından yakalanmıyordu.
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
// Kayıt tipleri
// ============================================================================

record Message(string Role, string Content);

record ChatResult(
    string Text,
    string Reasoning,
    int PromptTokens,
    int CompletionTokens,
    int ReasoningTokens,
    double TotalMs,
    string FinishReason);   // "stop" | "length" | ... ; "" = sunucu bildirmedi

record StreamResult(
    string Text,
    string Reasoning,
    int PromptTokens,
    int CompletionTokens,
    int ReasoningTokens,
    double TtftAnyMs,       // -1 = hiç token gelmedi, ölçülemedi
    double TtftContentMs,   // -1 = hiç görünür içerik üretilmedi
    double TotalMs,
    string FinishReason,
    bool CompletionTokensEstimated);   // true = sunucu usage göndermedi, TAHMİN


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

    /// <summary>
    /// Belirlilik katsayısı. LinearFit tek başına uyumun İYİ olup olmadığını
    /// söylemiyordu: den ~ 0 iken sessizce eğim 0 dönüyor ve bu değer gerçek
    /// ölçüm gibi raporlanıyordu. Katsayıyı R² ile birlikte bas.
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
// Metin yardımcıları
// ============================================================================

static class Text
{
    public static string Trunc(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";

        // UTF-16 kod birimiyle kesmek surrogate çiftini bölebilir; sınırı bir
        // birim geri alıp yarım karakter üretmiyoruz.
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
    /// Aynı ada VARSA satırları ekler, üzerine yazmaz. Her satırda run_id
    /// olduğu için koşular gerçekten karşılaştırılabilir hale geliyor —
    /// eskiden File.WriteAllText ile her koşu bir öncekini uyarısız siliyor,
    /// buna rağmen "koşular karşılaştırılabilir" deniyordu.
    ///
    /// Başlık değiştiyse (şema güncellendi) eski dosyaya eklemek veriyi
    /// bozar; o durumda eskisi .bak olarak saklanıp yeni dosya açılıyor.
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
                Console.WriteLine($"   NOT: {name}.csv şeması değişmiş, eskisi {name}.csv.bak olarak saklandı.");
            }
        }

        var sb = new StringBuilder();
        if (!append) sb.AppendLine(headerLine);
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Escape)));

        // BOM: Excel Türkçe karakterleri doğru okusun.
        if (append)
            File.AppendAllText(path, sb.ToString(), new UTF8Encoding(true));
        else
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));

        Console.WriteLine($"-> {path}{(append ? $" (+{rows.Count} satır eklendi)" : "")}");
    }

    static string Escape(string v)
    {
        v ??= "";

        // '\r' de tırnaklanmalı: tek başına gelen bir CR, BOM'lu (Excel hedefli)
        // dosyada satır yapısını bozuyordu.
        var needsQuote = v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r');

        // Formül enjeksiyonu: '=', '+', '-', '@' ile başlayan hücreleri Excel
        // formül sanıyor. ModelId ve elle doldurulan kolonlar sunucudan/insandan
        // geliyor, tırnak içine alıp başına ' koyarak nötrleştiriyoruz.
        if (v.Length > 0 && (v[0] is '=' or '+' or '@'))
            return "\"'" + v.Replace("\"", "\"\"") + "\"";

        return needsQuote ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
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

    public const string Usage =
        "Kullanım: dotnet run -- [--url <adres>] [--model <id>] [--out <klasör>] " +
        "[--doc-words <sayı>] [1..6 ...]";

    /// <summary>
    /// Argüman ayrıştırma. Tanınmayan her şey artık HATA: '--modle qwen' gibi bir
    /// yazım hatası sessizce yutulup otomatik model keşfine düşürüyordu, 'dotnet
    /// run -- 7' ise hiçbir deney seçmediği için hepsini koşturuyordu.
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
                        errors.Add($"{arg} bir değer bekliyor ama argüman listesi bitti");
                        break;
                    }

                    var value = args[++i];

                    if (arg == "--url") url = value;
                    else if (arg == "--model") model = value;
                    else if (arg == "--out") outDir = value;
                    else if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out docWords) || docWords < 1)
                        errors.Add($"--doc-words pozitif bir tamsayı olmalı, '{value}' geldi");

                    break;

                default:
                    if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    {
                        if (n is >= 1 and <= 6) exps.Add(n);
                        else errors.Add($"deney numarası 1-6 aralığında olmalı, '{n}' geldi");
                    }
                    else
                    {
                        errors.Add($"tanınmayan argüman: '{arg}'");
                    }

                    break;
            }
        }

        if (errors.Count > 0)
            throw new ArgumentException(string.Join("\n  - ", errors.Prepend("Argüman hatası:")));

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