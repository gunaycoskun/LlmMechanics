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
| Tarih | 08.09.2026 |

Ölçüm aracı: bağımlılıksız tek dosyalık C# konsol uygulaması (`LlmMechanics/Program.cs`).
Ham `HttpClient` kullanıldı; SDK/abstraction bilinçli olarak kullanılmadı, çünkü
abstraction tam da ölçülmek istenen sağlayıcı farklarını normalize eder.

### Metodoloji notları

- Her koşu öncesi 3 çağrılık **warm-up** yapıldı ve ölçüme dahil edilmedi
  (model yükleme, CUDA context init, kernel derlemesi ilk çağrılarda outlier üretir).
- Latency deneylerinde her prompt'un başına benzersiz bir GUID eklendi.
  Bu **KV cache hit'ini bilerek kırıyor**; kırılmazsa ikinci çağrıdan itibaren
  prefill atlanır ve sahte bir hızlanma ölçülür. Cache etkisi Deney 4'te
  ayrıca ve bilerek ölçüldü.
- **Medyan** raporlanıyor, ortalama değil: GPU'da masaüstü kompozisyonu ve
  termal dalgalanma arada spike üretir ve ortalamayı bozar. Kuyruk davranışı
  için p95 ayrıca verildi.

### Bilinen sınır: reasoning kapatılamadı

`qwen3.5-9b` hibrit bir reasoning modeli. Düşünme token'larını `delta.content`
içinde değil ayrı bir `reasoning_content` alanında yayınlıyor.

Düşünmeyi kapatmak için üç yol denendi, **üçü de etkisiz kaldı**:
`chat_template_kwargs.enable_thinking=false`, `/no_think` sistem mesajı, ve
LM Studio'nun `Reasoning Budget = 0` ayarı. Ölçümler düşünme açık koşuldu.

Bu latency katsayılarını **geçersizleştirmiyor**: GPU için bir token'ın düşünme
mi cevap mı olduğu fark etmez, decode maliyeti aynıdır. Deney 2'de beş farklı
çıktı uzunluğunda tok/s'in 119–121 bandında kalması bunu doğruluyor.

---

## Bulgu 1 — Türkçe token cezası: ortalama 1.26, en kötü 1.50

Anlamca eşdeğer Türkçe/İngilizce metin çiftleri, `max_tokens=1` ile tek çağrı.

| metin tipi | TR token | EN token | oran |
|---|---:|---:|---:|
| kısa cümle | 31 | 24 | 1.292 |
| teknik paragraf | 72 | 60 | 1.200 |
| **belge listesi** | **39** | **26** | **1.500** |
| sayısal | 56 | 50 | 1.120 |
| uzun madde | 92 | 76 | 1.211 |
| **ortalama** | | | **1.264** |

Gemini referansı 1.33 idi. Qwen'in çok dilli vocabulary'si bir miktar avantajlı,
ama fark küçük — "Qwen Türkçeyi çözmüş" denemez.

**Yorum uyarısı:** `prompt_tokens` chat template overhead'ini de içeriyor
(~15 token, iki tarafta da sabit). Sabit terim hem payı hem paydayı şişirdiği
için ölçülen oran gerçek metin-içi orandan **düşüktür**. Bu tablo alt sınırı verir.

**Karar:** Chunk boyutu hesabı ortalamaya değil **en kötü duruma** göre yapılacak.
En yüksek ceza kısa ve terim yoğun listelerde (1.50) ve hakediş eki listeleri
tam bu formatta. 1.26 ile plan yapmak, gerçek belgelerde %19 taşma riski demek.

---

## Bulgu 2 — 1 output token ≈ 42 input token

İki kollu ölçüm: A kolunda input değişken/output sabit, B kolunda tersi.
TTFT sadece streaming ile ölçülebildiği için her iki kol da streaming.

**A kolu — prefill**

| input token | ttft (ms) | total p50 (ms) |
|---:|---:|---:|
| 64 | 103.4 | 633.0 |
| 103 | 107.1 | 635.5 |
| 268 | 136.7 | 667.0 |
| 883 | 258.8 | 793.7 |
| 2519 | 579.0 | 1123.6 |

**B kolu — decode**

| output token | total p50 (ms) | tok/s |
|---:|---:|---:|
| 32 | 361.8 | 120.8 |
| 64 | 625.7 | 120.5 |
| 128 | 1161.3 | 120.1 |
| 256 | 2239.8 | 119.3 |
| 512 | 4380.3 | 119.5 |

En küçük kareler uyumu:

```
total ≈ 80 ms + 0.2014 × input_tokens + 8.378 × output_tokens
                                        └─ ~119 tok/s
```

**1 output token, 42 input token maliyetinde.**

Sebep fiziksel: prefill input token'larını paralel işler (compute-bound),
decode ise output token'larını sırayla üretir ve her adımda tüm model
ağırlıklarını bellekten okur (memory-bandwidth-bound). B kolunda tok/s'in
beş ölçümde 119–121 arasında kalması, ölçümün gerçekten bellek bant genişliği
sınırını yakaladığını gösteriyor.

**Kararlar:**
- Prompt kısaltmak neredeyse anlamsız bir optimizasyon. 2519 token'lık bir
  prompt sadece 507 ms prefill demek.
- RAG'de `top_k` cömert tutulabilir. 3 yerine 10 chunk göndermenin latency
  maliyeti ~100 ms; retrieval kalitesi kazancı bunu fazlasıyla karşılar.
- "Kısa ve öz cevap ver" bir üslup tercihi değil, **performans kararı**.
- Reasoning modelleri bu ekonomide pahalı: düşünme token'ları output tarafında,
  yani en pahalı tarafta sayılıyor.

---

## Bulgu 3 — Prompt sıralaması %91 latency farkı yaratıyor

İki ayrı gerçek var ve karıştırılmamalı:

- **Protokol seviyesi:** her tur tüm geçmiş yeniden gönderilir, token maliyeti
  kuadratik birikir. Bu değişmez.
- **Sunucu seviyesi:** aynı prefix'le devam eden istekte prefill yeniden
  hesaplanmaz, KV cache'ten gelir.

~4200 token'lık bir doküman system mesajına konuldu, 5 turluk konuşma yürütüldü.
Tek fark: `cache_dusmani` kolda system mesajının başına her turda 32 karakterlik
bir GUID eklendi.

| tur | cache_dostu ttft | cache_dusmani ttft |
|---:|---:|---:|
| 1 | 852 ms | 914 ms |
| 2 | **81 ms** | 900 ms |
| 3 | **84 ms** | 918 ms |
| 4 | **85 ms** | 934 ms |
| 5 | **90 ms** | 968 ms |

Ortalama (tur 2–5): **85 ms vs 930 ms — %91 kazanç.**

Toplam sürede de aynı: 1365 ms vs 2200 ms.

### İki deney birbirini doğruluyor

Deney 2'nin prefill katsayısıyla bu sonucu önceden hesaplayabiliriz:

```
tahmin : 4200 token × 0.2014 ms/token = 845.9 ms
ölçüm  : 930 − 85                     = 845.0 ms
```

Bir milisaniye fark. İki bağımsız deneyin aynı sayıyı vermesi, ölçüm
metodolojisinin doğru kurulduğunun kanıtı.

**Karar:** Prompt'ta **sabit içerik başta, değişken içerik sonda.**
System prompt + sözleşme metni önce, kullanıcı sorusu sonra. Timestamp, request-id,
oturum bilgisi gibi her istekte değişen alanların prompt'un başına konması
prefix eşleşmesini bozar ve her turda ~850 ms yakar. Bu bir stil tercihi değil,
ölçülmüş bir performans kuralı.

---

## Bulgu 4 — Determinizm sampling'den gelir, seed'den değil

Aynı prompt 20 kez, dört kolda.

| kol | benzersiz çıktı | ort. uzunluk |
|---|---:|---:|
| temp=0.0, seed=42 | 1/20 | 113 kar |
| temp=0.0, seed yok | 1/20 | 113 kar |
| temp=0.8, seed yok | 20/20 | 198 kar |
| temp=1.8, seed yok | 20/20 | 211 kar |

`temp=1.8` bir **kontrol kolu**: temperature parametresinin gerçekten modele
ulaştığını doğruluyor. Ulaşmasaydı orada da tek tip çıktı görülürdü.

Asıl bulgu: `temp=0`'da **seed verilmese de** çıktı deterministik. Determinizmi
sağlayan şey seed değil, greedy sampling (her adımda en yüksek olasılıklı token).
Seed yalnızca `temp>0` iken anlamlı.

Gemini'de gözlenen "10/10 farklı çıktı" modelin doğası değil, sampling ayarıydı.
Model ağırlıkları sabit bir fonksiyondur; non-determinism sampling'den ve sunucu
tarafı batching'den gelir.

**Açık soru (Faz 5'e devir):** Bu ölçüm sıralı, tek tek çağrılarla yapıldı.
Eşzamanlı batching altında GPU'da floating-point toplama sırası değişebilir ve
`temp=0`'da bile determinizm bozulabilir. Agent replay tasarlanırken yeniden
ölçülmeli.

---

## Bulgu 5 — Doyum 16 eşzamanlılıkta

Lokalde rate limit yok; sınır GPU'nun kendisi.

| paralel | toplam tok/s | istek başına tok/s | p50 (ms) | p95 (ms) |
|---:|---:|---:|---:|---:|
| 1 | 113.6 | 113.6 | 1758 | 1758 |
| 2 | 111.5 | 55.7 | 2699 | 3587 |
| 4 | 172.3 | 43.1 | 3651 | 4643 |
| 8 | 257.0 | 32.1 | 6218 | 6225 |
| **16** | **432.2** | **27.0** | 6699 | 7403 |
| 32 | 445.1 | 13.9 | 10847 | 14378 |

16 → 32 geçişinde toplam throughput yalnızca %3 artarken p95 latency ikiye
katlanıyor. **Doyum noktası 16.**

İki ayrıntı:

- **1 → 2 geçişinde kazanç yok.** Batching iki istekte henüz devreye girmiyor,
  istekler sadece sıra bekliyor. Gerçek batching kazancı 4'ten sonra başlıyor.
- **Kapasite planı toplam throughput'tan değil, istek başına düşen değerden
  çıkar.** 16 eşzamanlı kullanıcıda her biri tek kullanıcının dörtte biri hızda
  çalışıyor.

### Ölçtüğün sınır, en dar olan sınırdır

İlk koşuda eğri 8'den sonra düzleşiyordu ve bu GPU doyumu sanıldı. Gerçek sebep
LM Studio'nun `Max Concurrent Predictions: 4` ayarıydı — yani bir konfigürasyon
satırı. Ayar 16'ya çekildiğinde throughput 208 → 445 tok/s'e çıktı.

Bu kendi başına bir bulgu: kapasite ölçerken önce **yapay tavanların kaldırıldığını
doğrula**, yoksa donanım kararını bir config satırına dayandırırsın.

---

## Bulgu 6 — Kalite tabanı ve reasoning'in maliyeti

Sekiz mevzuat sorusu, her biri iki kez (düşünme açık / kapalı), elle değerlendirilmek
üzere `06_kalite_spot_kontrolu.md` dosyasına dökülüyor. Otomatik skor **bilinçli
olarak üretilmiyor** — gerçek eval harness'ı Faz 3-4'ün işi.

Model mevzuat üzerine eğitilmedi; hatalar beklenen sonuç ve RAG'in neden gerekli
olduğunun kanıtı.

Tek bir örnek, ölçeği gösteriyor. "Hakediş nedir? Tek cümle." sorusuna:

```
reasoning : 1016 token
content   :   25 token
oran      : 40:1
```

Deney 2'nin katsayısıyla çevirisi:

```
1016 × 8.378 ms = 8.5 saniye  (kullanıcının görmediği)
  25 × 8.378 ms = 0.2 saniye  (kullanıcının gördüğü)
```

Kullanıcı 8.5 saniye boş ekrana bakıyor. `ttftAny` ile `ttftContent`'in neden
ayrı ölçülmesi gerektiği bu: timeout bütçesi `ttftContent`'e göre kurulmazsa
sistem sağlıklı istekleri iptal eder.

Üstelik 1016 token düşünen model yanlış cevap verdi (hakedişi iş bitiminde
ödenen bir tutar olarak tanımladı; hakediş ara dönemlerde ödenir).
**Reasoning, alan bilgisi eksikliğini kapatmıyor.**

---

## Ölçümün kendisi ölçümü bozabilir

Bir ara koşuda Deney 1'in tüm token sayıları tam 8 arttı ve TR/EN oranı
1.264'ten 1.214'e düştü. Sebep: düşünmeyi kapatmak için denenen `/no_think`
sistem mesajı fallback'i açık kalmış ve sonraki tüm çağrılara 8 token eklemişti.
Sabit terim hem payı hem paydayı şişirdiği için oranı yapay olarak 1'e yaklaştırdı.

Kayda değer, çünkü hata **sessizdi**: sayılar makul görünüyordu, sadece yanlıştı.
Aynı sınıf hatanın ilk örneği v1'deki `reasoning_content` eksikliğiydi — TTFT
ölçümü çalışıyor göründü, aslında `total`'i ölçüyordu.

**Ders:** ölçüm aracının kendisi ölçülen sistemin bir parçasıdır. Her koşu
öncesi bir ön kontrol adımı (`ÖN KONTROL` bloğu) bu yüzden var.

---

## Sonraki fazlara devreden kararlar

| Karar | Dayanak |
|---|---|
| Chunk boyutu en kötü durum çarpanı 1.50 ile hesaplanacak | Bulgu 1 |
| RAG `top_k` cömert; prompt kısaltma optimizasyonu düşük öncelikli | Bulgu 2 |
| Cevap uzunluğu sınırlaması birincil latency kaldıracı | Bulgu 2 |
| Prompt düzeni: sabit içerik başta, değişken içerik sonda | Bulgu 3 |
| Deterministik çıktı gereken yerde `temperature=0`; seed ikincil | Bulgu 4 |
| Lokal kapasite tavanı 16 eşzamanlı istek | Bulgu 5 |
| Timeout bütçesi `ttftContent` üzerinden kurulacak | Bulgu 6 |
| Reasoning modeli düşük latency senaryolarında kullanılmayacak | Bulgu 6 |

## Açık kalanlar

- Eşzamanlı batching altında determinizm ölçülmedi (Faz 5).
- Kalite değerlendirmesi elle yapılacak; otomatik eval Faz 3-4.
- Non-reasoning bir instruct modeliyle karşılaştırma koşusu yapılmadı.
- Embedding modeli (`text-embedding-qwen3-embedding-0.6b`) yüklendi ama
  bu fazda ölçülmedi.

---

## Çalıştırma

```bash
cd LlmMechanics
dotnet run -- --model qwen/qwen3.5-9b            # tüm deneyler
dotnet run -- --model qwen/qwen3.5-9b 2 4        # seçili deneyler
dotnet run -- --model qwen/qwen3.5-9b --doc-words 1000
```

Çıktılar `results/` altına CSV olarak yazılır. Her satır `run_id` ile
damgalanır, koşular karşılaştırılabilir.
