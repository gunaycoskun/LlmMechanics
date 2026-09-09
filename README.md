# LlmMechanics
Bu proje LLM mekanikleri ve ölçüm değerlendirmeleri hakkındadır. AI desteği kullanılmıştır.

# Faz 1 — LLM Mekanikleri: Lokal Inference Ölçümleri

Bu dizin, bir LLM'in **ne söylediğini** değil **nasıl davrandığını** ölçen altı deney
içeriyor. Amaç bir demo değil, sonraki fazlarda alınacak mimari kararların
sayısal zeminini kurmak.

Ölçümler önce Google Gemini API üzerinde yapıldı, sonra lokal inference'a taşındı.
Taşıma sebebi maliyet değil **gizlilik**: kamu ihale ve hakediş evrakı dış API'ye
gönderilemez. Bu karar [ADR-0004](../../docs/adr/0004-local-inference-lm-studio.md)'te.

---

## Ortam

| | |
|---|---|
| CPU | AMD Ryzen 7 9800X3D (8 çekirdek / 16 mantıksal) |
| RAM | 30.9 GB |
| GPU | NVIDIA GeForce RTX 5070 Ti, 15.92 GB VRAM, CUDA |
| Sunucu | LM Studio 0.4.23, llama.cpp CUDA 12 backend |
| Model | `qwen/qwen3.5-9b`, GGUF, Q4_K_M, 6.55 GB |
| Context | 8192 token |
| GPU offload | 32/32 katman (tam) |
| Max Concurrent Predictions | 16 |
| Tarih | 09.09.2026 |

Ölçüm aracı: bağımlılıksız tek dosyalık C# konsol uygulaması (`LlmMechanics/Program.cs`).
Ham `HttpClient` kullanıldı; SDK/abstraction bilinçli olarak kullanılmadı, çünkü
abstraction tam da ölçülmek istenen sağlayıcı farklarını normalize eder.

### Metodoloji

- Her koşu başında 3 çağrılık **warm-up** yapılıyor ve ölçüme dahil edilmiyor
  (model yükleme, CUDA context init, kernel derlemesi ilk çağrılarda outlier üretir).
  Warm-up ön kontrolden **önce** koşuyor: tersi olduğunda ön kontrolün bastığı
  ttftAny soğuk maliyeti gösteriyordu (2413 ms'e karşı 65 ms) ve yanlış bir
  taban çizgisi okunuyordu.
- Latency deneylerinde her prompt'un başına benzersiz bir GUID ekleniyor.
  Bu **KV cache hit'ini bilerek kırıyor**; kırılmazsa ikinci çağrıdan itibaren
  prefill atlanır ve sahte bir hızlanma ölçülür. Cache etkisi Deney 4'te
  ayrıca ve bilerek ölçülüyor.
- **Medyan** raporlanıyor, ortalama değil: GPU'da masaüstü kompozisyonu ve
  termal dalgalanma arada spike üretir ve ortalamayı bozar.
- Ölçülemeyen değerler `-1` olarak kaydediliyor, `0`'a düşürülmüyor. `0` yazmak
  "anında geldi" gibi okunur ve CSV'yi sessizce yalanlar.

### Bilinen sınır: reasoning kapatılamadı

`qwen3.5-9b` hibrit bir reasoning modeli. Düşünme token'larını `delta.content`
içinde değil ayrı bir `reasoning_content` alanında yayınlıyor.

Düşünmeyi kapatmak için üç yol denendi, **üçü de etkisiz kaldı**:
`chat_template_kwargs.enable_thinking=false`, `/no_think` sistem mesajı, ve
LM Studio'nun `Reasoning Budget = 0` ayarı (model yeniden yüklenerek). Ölçümler
düşünme açık koşuldu.

Bu latency katsayılarını **geçersizleştirmiyor**: GPU için bir token'ın düşünme
mi cevap mı olduğu fark etmez, decode maliyeti aynıdır. Deney 2'de beş farklı
çıktı uzunluğunda tok/s'in 118.7–120.6 bandında kalması bunu doğruluyor.

**Etkilediği yer Deney 6:** düşünme açık/kapalı karşılaştırması yapılamadı.

---

## Bulgu 1 — Türkçe token cezası: net %39, en kötü %81

Anlamca eşdeğer Türkçe/İngilizce metin çiftleri, `max_tokens=1` ile tek çağrı.

Chat template her prompt'a sabit bir maliyet ekliyor. Bu sabit **hem payı hem
paydayı** şişirdiği için ham oranı yapay olarak 1'e yaklaştırıyor. Tek karakterlik
bir prompt göndererek sabiti ölçtük: **10 token** (`.` → 11 prompt_tokens).
Net oran, bu sabit çıkarıldıktan sonraki gerçek metin-içi ceza.

| metin tipi | TR | EN | ham oran | net oran |
|---|---:|---:|---:|---:|
| kısa cümle | 31 | 24 | 1.292 | 1.500 |
| teknik paragraf | 72 | 60 | 1.200 | 1.240 |
| **belge listesi** | **39** | **26** | **1.500** | **1.812** |
| sayısal | 56 | 50 | 1.120 | 1.150 |
| uzun madde | 92 | 76 | 1.211 | 1.242 |

```
Ham oran : ortalama 1.264 | en kötü 1.500 | token-ağırlıklı 1.229
Net oran : ortalama 1.389 | en kötü 1.812
```

Gemini referansı 1.33 idi — ama o da ham oran olarak ölçülmüştü, yani
karşılaştırma elmayla elma değil. Aynı düzeltme uygulanmadan iki model
kıyaslanamaz.

**Karar:** Chunk boyutu hesabı **net en kötü duruma (1.81)** göre yapılacak.
En yüksek ceza kısa ve terim yoğun listelerde ve hakediş eki listeleri tam bu
formatta. Ham ortalama 1.26 ile plan yapmak gerçek belgelerde %44 taşma riski demek.

---

## Bulgu 2 — 1 output token ≈ 44 input token

İki kollu ölçüm: A kolunda input değişken/output tavanı sabit, B kolunda tersi.
Her nokta 5 tekrar.

**A kolu — prefill** (temperature=0, max_tokens=64)

| input token | ttftAny (ms) | total p50 (ms) |
|---:|---:|---:|
| 55 | 71.4 | 609.2 |
| 95 | 80.3 | 608.3 |
| 261 | 101.7 | 634.8 |
| 874 | 228.9 | 776.3 |
| 2510 | 541.8 | 1104.3 |

**B kolu — decode** (temperature=0.7)

| output token | total p50 (ms) | tok/s |
|---:|---:|---:|
| 32 | 337.2 | 118.7 |
| 64 | 600.8 | 120.6 |
| 128 | 1141.7 | 119.8 |
| 256 | 2211.0 | 119.6 |
| 512 | 4365.5 | 119.1 |

En küçük kareler uyumu:

```
total ≈ 54 ms + 0.1925 × input_tokens + 8.395 × output_tokens
                  R²=1.000               R²=1.000  (~119.1 tok/s)
```

**1 output token, 44 input token maliyetinde.**

### Prefill katsayısı neden ttft üzerinden ölçülüyor

İki farklı yöntem iki farklı sayı veriyor:

```
a_ttft  = 0.1925 ms/token   (R²=1.000)  ← kullanılan
a_total = 0.2050 ms/token   (R²=0.999)
```

Sebep: A kolunda `max_tokens` bir **tavan**, sabit değil. Çıktı uzunluğu input ile
birlikte oynarsa decode terimi, total'e dayalı eğime sızar — klasik atlanmış
değişken sapması. `ttftAny` prefill'in bittiği anı damgalar ve decode'dan tamamen
bağımsızdır; prefill katsayısının doğru ölçüm aracı budur.

Bu koşuda A kolunda çıktı uzunluğu her noktada tam 64'te sabit kaldı (`out± = 0`),
o yüzden fark küçük. Sabit kalmasaydı `a_total` belirgin biçimde şişerdi.

### Fiziksel sebep

Prefill input token'larını **paralel** işler (compute-bound). Decode output
token'larını **sırayla** üretir ve her adımda tüm model ağırlıklarını bellekten
okur (memory-bandwidth-bound). B kolunda tok/s'in beş ölçümde 118.7–120.6
arasında kalması, ölçümün gerçekten bellek bant genişliği sınırını yakaladığını
gösteriyor.

**Kararlar:**
- Prompt kısaltmak düşük getirili bir optimizasyon. 2510 token'lık bir prompt
  sadece 483 ms prefill demek.
- RAG'de `top_k` cömert tutulabilir. 3 yerine 10 chunk göndermenin latency
  maliyeti ~100 ms; retrieval kalitesi kazancı bunu fazlasıyla karşılar.
- "Kısa ve öz cevap ver" bir üslup tercihi değil, **birincil latency kaldıracı**.
- Reasoning modelleri bu ekonomide pahalı: düşünme token'ları output tarafında,
  yani 44 kat pahalı tarafta sayılıyor.

---

## Bulgu 3 — Prompt sıralaması %91 latency farkı yaratıyor

İki ayrı gerçek var ve karıştırılmamalı:

- **Protokol seviyesi:** her tur tüm geçmiş yeniden gönderilir, token maliyeti
  birikir. Bu değişmez.
- **Sunucu seviyesi:** aynı prefix'le devam eden istekte prefill yeniden
  hesaplanmaz, KV cache'ten gelir.

~4200 token'lık bir doküman system mesajına konuldu, 5 tur soru soruldu.
Tek fark: `cache_dusmani` kolda system mesajının **başına** her turda 32 karakterlik
bir GUID eklendi.

| tur | cache_dostu ttftAny | cache_dusmani ttftAny |
|---:|---:|---:|
| 1 | 851 ms | 885 ms |
| 2 | **85 ms** | 893 ms |
| 3 | **82 ms** | 909 ms |
| 4 | **82 ms** | 924 ms |
| 5 | **85 ms** | 907 ms |

Tur 1 hariç medyan: **83 ms vs 908 ms — 825 ms fark, %91 kazanç.**
Toplam sürede de aynı: 1345 ms vs 2174 ms.

### İki deney birbirini doğruluyor

Deney 2'nin prefill katsayısıyla bu sonuç önceden hesaplanabilir:

```
Deney 4 (ölçüm)  : 825 ms / 4244 token = 0.1944 ms/token
Deney 2 (bağımsız): a_ttft             = 0.1925 ms/token
fark: %1
```

İki bağımsız deney, iki farklı yöntem, aynı fiziksel sabit. Ölçüm metodolojisinin
doğru kurulduğunun kanıtı bu.

### Cache hit bedava değil

```
cache_dostu   : 0.0198 ms/token
cache_dusmani : 0.2140 ms/token
```

Cache hit prefill'i sıfırlamıyor, ~10 kat ucuzlatıyor. Kalan maliyet cache
lookup'ın kendisi. "Cache'e düşerse maliyet yok" varsayımı yanlış.

**Karar:** Prompt'ta **sabit içerik başta, değişken içerik sonda.**
System prompt + sözleşme metni önce, kullanıcı sorusu sonra. Timestamp, request-id,
oturum bilgisi gibi her istekte değişen alanların prompt'un başına konması
prefix eşleşmesini bozar ve her turda ~825 ms yakar. Bu bir stil tercihi değil,
ölçülmüş bir performans kuralı.

### Bu deneyin sınırı

On turun **onunda da** asistan cevabı boş geldi: 150 token'lık bütçenin tamamı
reasoning'e gitti ve geçmişe `(cevap yok)` eklendi.

KV cache karşılaştırması **geçerli** — iki kol da aynı koşullarda, tek değişken
GUID. Ama bu 5 turluk gerçek bir konuşma değil, 5 turluk bir prompt büyümesi
simülasyonu. Gerçek bir konuşmada asistan cevapları da geçmişe eklenip prompt'u
daha hızlı şişirirdi. **Kuadratik token birikimi iddiası bu veriyle
desteklenmiyor**; sadece cache davranışı gösteriliyor.

---

## Bulgu 4 — Determinizm sampling'den gelir, seed'den değil

Aynı prompt 20 kez, dört kolda (max_tokens=2500).

| kol | benzersiz | boş hariç | boş | ort. uzunluk |
|---|---:|---:|---:|---:|
| temp=0.0, seed=42 | 1/20 | 1/20 | 0 | 113 kar |
| temp=0.0, seed yok | 1/20 | 1/20 | 0 | 113 kar |
| temp=0.8, seed yok | 19/20 | 18/18 | 2 | 161 kar |
| temp=1.8, seed yok | 20/20 | 20/20 | 0 | 242 kar |

Asıl bulgu: `temp=0`'da **seed verilmese de** çıktı deterministik. Determinizmi
sağlayan şey seed değil, greedy sampling (her adımda en yüksek olasılıklı token).
Seed yalnızca `temp>0` iken anlamlı.

Gemini'de gözlenen "10/10 farklı çıktı" modelin doğası değil, sampling ayarıydı.
Model ağırlıkları sabit bir fonksiyondur.

`temp=1.8` bir **kontrol kolu**: temperature parametresinin gerçekten modele
ulaştığını doğruluyor. Ulaşmasaydı orada da tek tip çıktı görülürdü.

### Bu deneyin sınırları

- **Çeşitlilik metriği zayıf.** "Benzersiz" tam string eşitliği ve n'de doyuyor.
  temp=0.8 (19/20) ile temp=1.8 (20/20) arasında anlamlı bir ayrım yapamıyor.
  Kontrol kolu bu yüzden zayıf bir sinyal. Dereceli bir metrik (Levenshtein,
  self-BLEU) gerekirdi; ham çıktılar bu hesap için diske yazılıyor.
- **temp=0.8 kolu kirli:** 2 boş çıktı (bütçe reasoning'e gitti) ve 2 kesik
  çıktı (max_tokens tavanı). Çeşitlilik sayımı kesik metinleri karşılaştırıyor.
- **Batching ölçülmedi.** Çağrılar sıralı, aynı anda tek istek uçuyor. Sunucu
  tarafı batching'in GPU'da floating-point toplama sırasını değiştirmesi ve
  `temp=0`'da bile determinizmi bozması mümkün. Bu tasarımla ölçülemez.
  **Faz 5'te agent replay tasarlanırken yeniden ölçülmeli.**

---

## Bulgu 5 — Doyum 16 eşzamanlılıkta

Lokalde rate limit yok; sınır GPU'nun kendisi.

| paralel | toplam tok/s | istek başına tok/s | medyan (ms) | en yavaş (ms) |
|---:|---:|---:|---:|---:|
| 1 | 113.1 | 113.1 | 1767 | 1767 |
| 2 | 113.6 | 56.8 | 2665 | 3520 |
| 4 | 175.3 | 43.8 | 3561 | 4563 |
| 8 | 265.3 | 33.2 | 6024 | 6030 |
| **16** | **443.3** | **27.7** | 6494 | 7218 |
| 32 | 458.5 | 14.3 | 10512 | 13956 |

16 → 32 geçişinde toplam throughput yalnızca %3 artarken en yavaş istek
7218 → 13956 ms'e çıkıyor. **Doyum noktası 16.**

İki ayrıntı:

- **1 → 2 geçişinde kazanç yok** (113.1 → 113.6). Batching iki istekte henüz
  devreye girmiyor, istekler sadece sıra bekliyor. Gerçek kazanç 4'ten sonra.
- **Kapasite planı toplam throughput'tan değil, istek başına düşen değerden
  çıkar.** 16 eşzamanlı kullanıcıda her biri tek kullanıcının dörtte biri
  hızda çalışıyor.

### Ölçtüğün sınır, en dar olan sınırdır

İlk koşuda eğri 8'den sonra düzleşiyordu ve bu GPU doyumu sanıldı. Gerçek sebep
LM Studio'nun `Max Concurrent Predictions: 4` varsayılanıydı — yani bir
konfigürasyon satırı. Ayar 16'ya çekildiğinde throughput 208 → 445 tok/s'e çıktı.

Kapasite ölçerken **önce yapay tavanların kaldırıldığını doğrula**, yoksa donanım
kararını bir config satırına dayandırırsın. Test edilen en yüksek paralellik
seviyesi, sunucunun eşzamanlılık ayarından küçük olmalı; değilse "doyum noktası"
dairesel bir sonuçtur.

### Bu deneyin sınırları

- **"En yavaş" kolonu bir kuyruk istatistiği değil.** n ≤ 20 iken nearest-rank
  p95 matematiksel olarak maksimuma eşit. Tek batch'in en yavaş isteğini
  gösteriyor, dağılımın kuyruğunu değil.
- **Tekrar yok, karşı-dengeleme yok.** Seviyeler tek koşu ve daima artan sırada
  ölçüldü; en yüksek paralellik daima en ısınmış GPU'da. Termal etki
  ayrıştırılamıyor.

---

## Bulgu 6 — Reasoning'in görünmez maliyeti

Sekiz mevzuat sorusu, elle değerlendirilmek üzere `06_kalite_spot_kontrolu.md`
dosyasına dökülüyor. Otomatik skor **bilinçli olarak üretilmiyor** — gerçek eval
harness'ı Faz 3-4'ün işi.

Model mevzuat üzerine eğitilmedi; hatalar beklenen sonuç ve RAG'in neden gerekli
olduğunun kanıtı.

**Planlanan karşılaştırma yapılamadı.** Düşünme açık/kapalı iki kol tasarlanmıştı;
düşünme kapatılamadığı için iki kol birebir aynı isteği gönderiyordu (ilk koşuda
8/8 soruda token sayıları özdeş çıktı). Harness artık bu durumu tespit edip tek
kol koşuyor.

Elde kalan tek somut ölçüm, ölçeği göstermeye yetiyor. "Hakediş nedir? Tek cümle."
sorusuna:

```
reasoning : 1016 token
content   :   25 token
oran      : 40:1
```

Deney 2'nin katsayısıyla çevirisi:

```
1016 × 8.395 ms = 8.5 saniye  (kullanıcının görmediği)
  25 × 8.395 ms = 0.2 saniye  (kullanıcının gördüğü)
```

Kullanıcı 8.5 saniye boş ekrana bakıyor. `ttftAny` ile `ttftContent`'in neden
ayrı ölçülmesi gerektiği bu: timeout bütçesi `ttftContent`'e göre kurulmazsa
sistem sağlıklı istekleri iptal eder.

Üstelik 1016 token düşünen model **yanlış cevap verdi** — hakedişi "iş tamamlanıp
kabul edildikten sonra ödenen" bir tutar olarak tanımladı; hakediş ara dönemlerde
ödenir. **Reasoning, alan bilgisi eksikliğini kapatmıyor.**

---

## Ölçümün kendisi ölçümü bozabilir

Bu fazda üç sessiz hata bulundu. Üçünün ortak özelliği: program çökmedi, sayılar
makul göründü, sadece yanlıştı.

**1. Okunmayan kanal.** İlk sürüm modelin `content` alanını okuyordu; bu model
düşünmeyi `reasoning_content` alanında yayınlıyor. Sonuç: tüm çıktılar boş
göründü ve TTFT damgası üretimin sonuna düştü, yani `ttft == total` oldu.
Deney 3'ün "1/20 benzersiz" sonucu aslında 20 adet boş string'in eşitliğiydi.

**2. Açık kalan fallback.** Düşünmeyi kapatmak için denenen `/no_think` sistem
mesajı işe yaramadı ama açık kaldı ve sonraki tüm çağrılara sabit bir tur ekledi.
Deney 1'in TR/EN oranı 1.264'ten 1.214'e düştü — sabit terim hem payı hem paydayı
şişirdiği için. Fallback artık işe yaramazsa otomatik geri alınıyor.

**3. Ölçülemeyeni ölçülmüş gibi kaydetmek.** `ttftAny` bulunamadığında `totalMs`'e
düşürülüyordu. Bu, birinci hatanın ürettiği patolojiyi bayraksız olarak yeniden
üretiyor ve regresyona gerçek ölçüm gibi giriyordu. Artık `-1` olarak kalıyor ve
hesaplamalardan dışlanıyor.

**Alınan önlem:** her koşu, ölçümden önce bir **ön kontrol** adımı çalıştırıyor —
reasoning hangi kanaldan geliyor, düşünme kapatılabiliyor mu, sunucu `usage`
gönderiyor mu. Ayrıca katsayılar R² ile birlikte raporlanıyor ve şüpheli
değerler (negatif overhead, düşük R², yöntemler arası %50'den fazla ayrışma)
açıkça uyarı olarak basılıyor.

Ölçüm aracının kendisi, ölçülen sistemin bir parçasıdır.

---

## Sonraki fazlara devreden kararlar

| Karar | Dayanak |
|---|---|
| Chunk boyutu net en kötü durum çarpanı **1.81** ile hesaplanacak | Bulgu 1 |
| RAG `top_k` cömert; prompt kısaltma düşük öncelikli | Bulgu 2 |
| Cevap uzunluğu sınırlaması birincil latency kaldıracı | Bulgu 2 |
| Prompt düzeni: sabit içerik başta, değişken içerik sonda | Bulgu 3 |
| Cache hit maliyeti sıfır değil (~10x ucuz), planlamada sıfır sayılmayacak | Bulgu 3 |
| Deterministik çıktı gereken yerde `temperature=0`; seed ikincil | Bulgu 4 |
| Lokal kapasite tavanı 16 eşzamanlı istek | Bulgu 5 |
| Timeout bütçesi `ttftContent` üzerinden kurulacak | Bulgu 6 |
| Reasoning modeli düşük latency senaryolarında kullanılmayacak | Bulgu 6 |

## Açık kalanlar

- Eşzamanlı batching altında determinizm ölçülmedi (Faz 5).
- Reasoning açık/kapalı kalite-maliyet karşılaştırması yapılamadı.
- Non-reasoning bir instruct modeliyle karşılaştırma koşusu yapılmadı.
- Kalite değerlendirmesi elle yapılacak; otomatik eval Faz 3-4.
- Embedding modeli (`text-embedding-qwen3-embedding-0.6b`) yüklendi ama
  bu fazda ölçülmedi.
- Deney 5'te tekrar ve karşı-dengeleme yok; termal etki ayrıştırılmadı.

---

## Çalıştırma

```bash
cd LlmMechanics
dotnet run -- --model qwen/qwen3.5-9b            # tüm deneyler
dotnet run -- --model qwen/qwen3.5-9b 2 4        # seçili deneyler
dotnet run -- --model qwen/qwen3.5-9b --doc-words 1000
```

Çıktılar `results/` altına yazılır. Her satır `run_id` ile damgalanır ve şema
aynıysa CSV'ye eklenir, üzerine yazılmaz — koşular karşılaştırılabilir. Şema
değişirse eski dosya `.bak` olarak saklanır. Elle doldurulan dosyalar
(`03_ornek_*.txt`, `06_kalite_spot_kontrolu.md`) hiçbir zaman ezilmez; varsa
`run_id` ekli bir kardeş dosyaya yazılır.
