# Kalite spot kontrolü — qwen/qwen3.5-9b — 20260908-160941

Her cevabı elle değerlendir: [D]oğru / [K]ısmen / [Y]anlış / [H]alüsinasyon

Not: bu model mevzuat üzerine eğitilmedi. Amaç RAG ÖNCESİ taban çizgisini
görmek — hatalar beklenen sonuçtur ve RAG'in neden gerekli olduğunun kanıtıdır.

Her soru iki kez soruluyor: düşünme AÇIK ve KAPALI. Reasoning token'ları
output tarafında sayılır (en pahalı taraf), o yüzden kalite katkısının
maliyeti hak edip etmediği ölçülebilir bir sorudur.

## Soru 1
**Prompt:** Hakediş raporu ile kesin hesap arasındaki fark nedir?

### düşünme AÇIK

**Hakediş Raporu** ve **Kesin Hesap**, inşaat, mimarlık ve özellikle kamu ihaleleri kapsamında yapılan sözleşmelerde kullanılan iki temel finansal belgedir. Temel farkları; zamanlama, kapsamı ve hukuki sonuçlarıdır.

İşte bu iki kavram arasındaki temel farkların detaylı karşılaştırması:

### 1. Tanım ve Zamanlama Farkı

*   **Hakediş (Ara Ödeme Raporu):**
    *   **Ne Zaman Verilir?** İşin yürütüldüğü süre boyunca, belirli aralıklarla (genellikle aylık veya kilometre bazlı) verilir.
    *   **Ne İçerir?** O tarihe kadar yapılan işlerin maliyetini, kullanılan malzemeleri ve tamamlanan aşamaları gösterir.
    *   **Amaç:** Yükleniciye nakit akışı sağlamak ve projenin ilerlemesini finansal olarak takip etmektir.

*   **Kesin Hesap (Son Ödeme / Kapanış Raporu):**
    *   **Ne Zaman Verilir?** İşin tamamlandığı, kabul edildiği ve garanti süresinin doldurulduğu (veya tamamlanacağı) son aşamada verilir.
    *   **Ne İçerir?** Tüm hakedişlerin toplamı, sözleşme değişiklikleri, ceza/prim hesaplamaları, eksik işler için yapılan düzeltmeler ve nihai maliyeti içerir.
    *   **Amaç:** Sözleşmenin finansal olarak tamamen kapanmasını sağlamak ve yüklenici ile alıcı arasındaki borç/alacak ilişkisini sonlandırmaktır.

### 2. İçerik ve Kapsam Farkı

| Özellik | Hakediş Raporu | Kesin Hesap |
| :--- | :--- | :--- |
| **Kapsam** | Sadece o döneme kadar yapılan işler. | Tüm proje ömrü (Başlangıçtan bitişe kadar). |
| **Değişiklikler** | Genellikle sadece mevcut sözleşme kapsamındaki işler. | Sözleşme değişiklikleri, ek işler ve iptal edilen işler dahil edilir. |
| **Cezalar/Primler** | Genellikle uygulanmaz (veya ön tahmin). | Kesin hesapta ceza kesintileri veya prim ödemeleri net olarak yansıtılır. |
| **Garanti** | Garanti süresi henüz başlamamış olabilir. | Garanti süresinin sonu veya tamamlanması ile ilişkilidir. |
| **Hukuki Durum** | Geçici borç/alacak ilişkisi yaratır. | Borç/alacak ilişkisini "kesinleştirir" (kapanır). |

### 3. Hukuki ve Mali Süreç Farkı

*   **Hakediş Süreci:** Yüklenici hakedışı sunar -> İhale Müdürlüğü/İşletme onaylar -> Ödeme yapılır -> Yeni hakediş hazırlanır. Bu döngü iş bitene kadar devam eder.
*   **Kesin Hesap Süreci:** İş bittiğinde yüklenici "Kesin Hesap" dosyasını sunar. Bu dosya, mali birim tarafından denetlenir (genellikle harcama denetimi). Denetimden geçtikten sonra "Son Ödeme" yapılır ve sözleşme resmi olarak kapatılır.

### 4. Neden Karıştırılmamalı?

Bazen **"Son Hakediş"** ile **"Kesin Hesap"** karıştırılabilir.
*   **Son Hakediş:** İş bitmeden hemen önceki, son dönemlik ödeme talebidir (Hakediş zincirinin son halkasıdır).
*   **Kesin Hesap:** Son hakediş dahil olmak üzere tüm işlerin, ceza/prim hesaplamalarının ve sözleşmenin tamamen bittiğini gösteren nihai belgedir.

### Özetle

**Hakediş**, "Şu ana kadar ne kadar yaptım, bana o kadarını ödeyin" demektir (Süreç içi).
**Kesin Hesap**, "İşi bitirdim, tüm hesaplamaları yaptım, kalan borcu ve son durumu netleştiriyorum, sözleşmeyi kapatıyorum" demektir (Süreç dışı/Kapanış).

Kamu ihalelerinde (4734 sayılı İhale Kanunu) **Kesin Hesap**ın onaylanması, yüklenicinin alacağı garantilerin iadesi ve yasal olarak borcunun tamamen sona ermesi için şarttır.

*22 in / 2186 out (reasoning: 1163) — 18371 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

### düşünme KAPALI

**Hakediş Raporu** ve **Kesin Hesap**, inşaat, mimarlık ve özellikle kamu ihaleleri kapsamında yapılan sözleşmelerde kullanılan iki temel finansal belgedir. Temel farkları; zamanlama, kapsamı ve hukuki sonuçlarıdır.

İşte bu iki kavram arasındaki temel farkların detaylı karşılaştırması:

### 1. Tanım ve Zamanlama Farkı

*   **Hakediş (Ara Ödeme Raporu):**
    *   **Ne Zaman Verilir?** İşin yürütüldüğü süre boyunca, belirli aralıklarla (genellikle aylık veya kilometre bazlı) verilir.
    *   **Ne İçerir?** O tarihe kadar yapılan işlerin maliyetini, kullanılan malzemeleri ve tamamlanan aşamaları gösterir.
    *   **Amaç:** Yükleniciye nakit akışı sağlamak ve projenin ilerlemesini finansal olarak takip etmektir.

*   **Kesin Hesap (Son Ödeme / Kapanış Raporu):**
    *   **Ne Zaman Verilir?** İşin tamamlandığı, kabul edildiği ve garanti süresinin doldurulduğu (veya tamamlanacağı) son aşamada verilir.
    *   **Ne İçerir?** Tüm hakedişlerin toplamı, sözleşme değişiklikleri, ceza/prim hesaplamaları, eksik işler için yapılan düzeltmeler ve nihai maliyeti içerir.
    *   **Amaç:** Sözleşmenin finansal olarak tamamen kapanmasını sağlamak ve yüklenici ile alıcı arasındaki borç/alacak ilişkisini sonlandırmaktır.

### 2. İçerik ve Kapsam Farkı

| Özellik | Hakediş Raporu | Kesin Hesap |
| :--- | :--- | :--- |
| **Kapsam** | Sadece o döneme kadar yapılan işler. | Tüm proje ömrü (Başlangıçtan bitişe kadar). |
| **Değişiklikler** | Genellikle sadece mevcut sözleşme kapsamındaki işler. | Sözleşme değişiklikleri, ek işler ve iptal edilen işler dahil edilir. |
| **Cezalar/Primler** | Genellikle uygulanmaz (veya ön tahmin). | Kesin hesapta ceza kesintileri veya prim ödemeleri net olarak yansıtılır. |
| **Garanti** | Garanti süresi henüz başlamamış olabilir. | Garanti süresinin sonu veya tamamlanması ile ilişkilidir. |
| **Hukuki Durum** | Geçici borç/alacak ilişkisi yaratır. | Borç/alacak ilişkisini "kesinleştirir" (kapanır). |

### 3. Hukuki ve Mali Süreç Farkı

*   **Hakediş Süreci:** Yüklenici hakedışı sunar -> İhale Müdürlüğü/İşletme onaylar -> Ödeme yapılır -> Yeni hakediş hazırlanır. Bu döngü iş bitene kadar devam eder.
*   **Kesin Hesap Süreci:** İş bittiğinde yüklenici "Kesin Hesap" dosyasını sunar. Bu dosya, mali birim tarafından denetlenir (genellikle harcama denetimi). Denetimden geçtikten sonra "Son Ödeme" yapılır ve sözleşme resmi olarak kapatılır.

### 4. Neden Karıştırılmamalı?

Bazen **"Son Hakediş"** ile **"Kesin Hesap"** karıştırılabilir.
*   **Son Hakediş:** İş bitmeden hemen önceki, son dönemlik ödeme talebidir (Hakediş zincirinin son halkasıdır).
*   **Kesin Hesap:** Son hakediş dahil olmak üzere tüm işlerin, ceza/prim hesaplamalarının ve sözleşmenin tamamen bittiğini gösteren nihai belgedir.

### Özetle

**Hakediş**, "Şu ana kadar ne kadar yaptım, bana o kadarını ödeyin" demektir (Süreç içi).
**Kesin Hesap**, "İşi bitirdim, tüm hesaplamaları yaptım, kalan borcu ve son durumu netleştiriyorum, sözleşmeyi kapatıyorum" demektir (Süreç dışı/Kapanış).

Kamu ihalelerinde (4734 sayılı İhale Kanunu) **Kesin Hesap**ın onaylanması, yüklenicinin alacağı garantilerin iadesi ve yasal olarak borcunun tamamen sona ermesi için şarttır.

*22 in / 2186 out (reasoning: 1163) — 18762 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

---

## Soru 2
**Prompt:** Fiyat farkı hesaplamasında kullanılan temel endeks nedir ve neden gereklidir?

### düşünme AÇIK

_(boş çıktı — hata)_

*29 in / 3000 out (reasoning: 3000) — 25873 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

### düşünme KAPALI

_(boş çıktı — hata)_

*29 in / 3000 out (reasoning: 3000) — 25899 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

---

## Soru 3
**Prompt:** İş artışı sözleşme bedelinin yüzde kaçına kadar yapılabilir?

### düşünme AÇIK

Türkiye'de iş artışı sözleşme bedelinin yüzde kaçına kadar yapılabilir sorusunun cevabı, **sözleşmenin türüne (kamu veya özel sektör)** ve **yapılan işin niteliğine** göre değişiklik gösterir. Yasal olarak tek bir "üçgen" yoktur ancak genel kabul gören sınırlar şöyledir:

### 1. Kamu İhaleleri (4734 Sayılı Kamu İhale Kanunu)
Kamu kurumları ve belediyelerle yapılan işlerde durum daha katıdır.

*   **Genel Kural:** Kamu ihale mevzuatına göre, sözleşme bedelinin **%10'unu** aşan iş artışları genellikle yeni bir ihale süreci gerektirir veya özel şartname hükümlerine tabi tutulur.
*   **Neden?** Bu kural, "ihaleyi bölme" (contract splitting) ve kamu kaynaklarının şeffaf kullanımı ilkesini korumak için konulmuştur. %10'un üzerinde artış olması durumunda, sözleşmenin asıl niteliği değişmiş sayılır ve yeni ihale yapılması gerekebilir.
*   **İstisnalar:** Bazı özel durumlar (örneğin; beklenmeyen zorunlu değişiklikler, tasarım değişikliği vb.) veya sözleşmede "iş artışı" için özel bir madde varsa, bu oran %10 ile %25 arasında esneklik gösterebilir ancak bu mutlaka yazılı onay ve ihale komisyonu kararıyla yapılır.

### 2. Özel Sektör (Türk Medeni Kanunu - TMK)
Özel şirketler veya bireyler arasındaki sözleşmelerde durum daha esnektir.

*   **Yasal Sınır Yoktur:** Türk Medeni Kanunu'nda iş artışının yüzdelik bir sınırı yoktur.
*   **Sözleşme Koşulları:** İş artışı, tarafların (işveren ve yüklenici) **mutabakatı** ile mümkündür. Sözleşmede "İş Artışı" maddesi varsa, bu maddeye göre işlem yapılır.
*   **Esas Değişiklik İlkesi:** Eğer iş artışı kadar büyük bir oranda olursa ki sözleşmenin **esas niteliğini değiştirir**, tarafların rızası olmadan tek başına yapılamaz. Ancak %10 veya %25 gibi sabit bir yasal sınır yoktur; bu tamamen tarafların anlaşmasına bağlıdır.

### İş Artışının Yapılması İçin Şartlar
Hangi sektör olursa olsun, iş artışı geçerli olması için şu şartların sağlanması gerekir:

1.  **Yazılı Onay:** İş artışının sözleşme bedeline yansıtılması mutlaka yazılı bir ek sözleşme veya "iş artışı onay belgesi" ile yapılmalıdır.
2.  **Bedel Belirleme:** Artan işin bedeli nasıl hesaplanacak (birim fiyat üzerinden mi, yoksa sabit bir rakam mı?) sözleşmede net belirtilmelidir.
3.  **Gerekli Olması:** İş artışı, asıl işi tamamlamak için gerekli veya zorunlu olmalıdır.
4.  **Zamanlama:** Genellikle işin bitiminden önce yapılması ve faturalandırılması gerekir.

### Özetle
*   **Kamu İhaleleri:** Genellikle **%10** (bazen %25'e kadar özel şartlarla) sınırı vardır. Aşılırsa yeni ihale gerekebilir.
*   **Özel Sözleşmeler:** Yasal bir sınır yoktur, ancak sözleşmede belirtilen oran ve tarafların mutabakatı esas alınır.

**Uyarı:** İş artışının yasal sınırları aşılmaması, vergisel (KDV) ve sigortalama açısından da önemlidir. Bu tür işlemler için mutlaka bir avukat veya mali müşavir ile sözleşme detayları incelenmelidir.

*26 in / 2841 out (reasoning: 1958) — 24497 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

### düşünme KAPALI

Türkiye'de iş artışı sözleşme bedelinin yüzde kaçına kadar yapılabilir sorusunun cevabı, **sözleşmenin türüne (kamu veya özel sektör)** ve **yapılan işin niteliğine** göre değişiklik gösterir. Yasal olarak tek bir "üçgen" yoktur ancak genel kabul gören sınırlar şöyledir:

### 1. Kamu İhaleleri (4734 Sayılı Kamu İhale Kanunu)
Kamu kurumları ve belediyelerle yapılan işlerde durum daha katıdır.

*   **Genel Kural:** Kamu ihale mevzuatına göre, sözleşme bedelinin **%10'unu** aşan iş artışları genellikle yeni bir ihale süreci gerektirir veya özel şartname hükümlerine tabi tutulur.
*   **Neden?** Bu kural, "ihaleyi bölme" (contract splitting) ve kamu kaynaklarının şeffaf kullanımı ilkesini korumak için konulmuştur. %10'un üzerinde artış olması durumunda, sözleşmenin asıl niteliği değişmiş sayılır ve yeni ihale yapılması gerekebilir.
*   **İstisnalar:** Bazı özel durumlar (örneğin; beklenmeyen zorunlu değişiklikler, tasarım değişikliği vb.) veya sözleşmede "iş artışı" için özel bir madde varsa, bu oran %10 ile %25 arasında esneklik gösterebilir ancak bu mutlaka yazılı onay ve ihale komisyonu kararıyla yapılır.

### 2. Özel Sektör (Türk Medeni Kanunu - TMK)
Özel şirketler veya bireyler arasındaki sözleşmelerde durum daha esnektir.

*   **Yasal Sınır Yoktur:** Türk Medeni Kanunu'nda iş artışının yüzdelik bir sınırı yoktur.
*   **Sözleşme Koşulları:** İş artışı, tarafların (işveren ve yüklenici) **mutabakatı** ile mümkündür. Sözleşmede "İş Artışı" maddesi varsa, bu maddeye göre işlem yapılır.
*   **Esas Değişiklik İlkesi:** Eğer iş artışı kadar büyük bir oranda olursa ki sözleşmenin **esas niteliğini değiştirir**, tarafların rızası olmadan tek başına yapılamaz. Ancak %10 veya %25 gibi sabit bir yasal sınır yoktur; bu tamamen tarafların anlaşmasına bağlıdır.

### İş Artışının Yapılması İçin Şartlar
Hangi sektör olursa olsun, iş artışı geçerli olması için şu şartların sağlanması gerekir:

1.  **Yazılı Onay:** İş artışının sözleşme bedeline yansıtılması mutlaka yazılı bir ek sözleşme veya "iş artışı onay belgesi" ile yapılmalıdır.
2.  **Bedel Belirleme:** Artan işin bedeli nasıl hesaplanacak (birim fiyat üzerinden mi, yoksa sabit bir rakam mı?) sözleşmede net belirtilmelidir.
3.  **Gerekli Olması:** İş artışı, asıl işi tamamlamak için gerekli veya zorunlu olmalıdır.
4.  **Zamanlama:** Genellikle işin bitiminden önce yapılması ve faturalandırılması gerekir.

### Özetle
*   **Kamu İhaleleri:** Genellikle **%10** (bazen %25'e kadar özel şartlarla) sınırı vardır. Aşılırsa yeni ihale gerekebilir.
*   **Özel Sözleşmeler:** Yasal bir sınır yoktur, ancak sözleşmede belirtilen oran ve tarafların mutabakatı esas alınır.

**Uyarı:** İş artışının yasal sınırları aşılmaması, vergisel (KDV) ve sigortalama açısından da önemlidir. Bu tür işlemler için mutlaka bir avukat veya mali müşavir ile sözleşme detayları incelenmelidir.

*26 in / 2841 out (reasoning: 1958) — 24526 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

---

## Soru 4
**Prompt:** Geçici kabul ile kesin kabul arasında hangi süre işler?

### düşünme AÇIK

Geçici kabul ile kesin kabul arasındaki süre, yasal ve teknik olarak **"Haklı Süre"** (veya Defect Liability Period) olarak adlandırılır.

Bu sürenin genel özellikleri şöyledir:

1.  **Süre:** Genellikle bu süre **1 yıl (12 ay)** olarak belirlenir. Ancak, ihale dokümanları ve sözleşmede işin niteliğine göre daha uzun veya farklı bir süre de yazılabilir.
2.  **Yasal Dayanak:** 4734 Sayılı Kamu İhale Kanunu ve ilgili yönetmelikler çerçevesinde, geçici kabulden sonra ortaya çıkabilecek eksikliklerin giderilmesi için bu süre tanınır.
3.  **Amaç:** Bu süre zarfında yüklenici firma, geçici kabul sırasında tespit edilen veya daha sonra ortaya çıkan hataları (eksiklikleri) gidermekle yükümlüdür. Tüm eksiklikler giderildiğinde ve denetimden geçtiğinde "Kesin Kabul" gerçekleşir.
4.  **Sözleşme Esası:** Sürenin tam olarak ne kadar süre olacağı, ihale şartnamesinde ve sözleşmede açıkça belirtilir.

**Özetle:** Standart inşaat işleri için bu süre genellikle **1 yıl**dır, ancak kesin hüküm sözleşmedeki "Haklı Süre" maddesidir.

*23 in / 2084 out (reasoning: 1777) — 17930 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

### düşünme KAPALI

Geçici kabul ile kesin kabul arasındaki süre, yasal ve teknik olarak **"Haklı Süre"** (veya Defect Liability Period) olarak adlandırılır.

Bu sürenin genel özellikleri şöyledir:

1.  **Süre:** Genellikle bu süre **1 yıl (12 ay)** olarak belirlenir. Ancak, ihale dokümanları ve sözleşmede işin niteliğine göre daha uzun veya farklı bir süre de yazılabilir.
2.  **Yasal Dayanak:** 4734 Sayılı Kamu İhale Kanunu ve ilgili yönetmelikler çerçevesinde, geçici kabulden sonra ortaya çıkabilecek eksikliklerin giderilmesi için bu süre tanınır.
3.  **Amaç:** Bu süre zarfında yüklenici firma, geçici kabul sırasında tespit edilen veya daha sonra ortaya çıkan hataları (eksiklikleri) gidermekle yükümlüdür. Tüm eksiklikler giderildiğinde ve denetimden geçtiğinde "Kesin Kabul" gerçekleşir.
4.  **Sözleşme Esası:** Sürenin tam olarak ne kadar süre olacağı, ihale şartnamesinde ve sözleşmede açıkça belirtilir.

**Özetle:** Standart inşaat işleri için bu süre genellikle **1 yıl**dır, ancak kesin hüküm sözleşmedeki "Haklı Süre" maddesidir.

*23 in / 2084 out (reasoning: 1777) — 17950 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

---

## Soru 5
**Prompt:** Yükleniciye ödenen avansın teminatı nasıl çözülür?

### düşünme AÇIK

Yükleniciye ödenen avansın teminatının (Avans Teminatı) çözülmesi süreci, Türkiye'de **4734 Sayılı Kamu İhale Kanunu (KİK)** ve buna bağlı **İhale ve Sözleşme Yönetmeliği** çerçevesinde düzenlenmiştir. Bu süreç hem kamu ihaleleri hem de özel hukuk sözleşmelerinde benzer mantıkla yürütülse de, "Yüklenici" terimi genellikle kamu inşaat işlerinde kullanıldığı için aşağıdaki açıklamalar öncelikle **Kamu İhale Kanunu** esaslarına göre hazırlanmıştır.

Avans teminatının çözülmesi (teminatın iadesi veya banka garantisinin iptali) şu adımlar ve koşullarla gerçekleşir:

### 1. Temel Koşul: Avansın Kademeli Tahsilatla İadesi
Avans teminatı, yüklenicinin avans tutarını sözleşme gereği ilerleyen ödemelerden (kademeli tahsilat) iade etmesi durumunda çözülür.
*   **Mekanizma:** Yükleniciye yapılan avans ödemesi, sonraki faturalarda veya ödeme dönemlerinde yükleniciden kesinti yoluyla geri alınır.
*   **Çözüm Zamanı:** Avans tutarı tamamen tahsil edildiği (geri alındığı) an, teminatın varlık sebebi ortadan kalkar ve çözülmesi gerekir.

### 2. Çözüm Sürecinin Adımları

**A. Yüklenici Tarafından Talep Edilmesi:**
Avansın tamamının tahsil edildiğini tespit eden yüklenici, sözleşme hükümlerine göre İhale Edene (Sahibi) yazılı bir **teminat çözümü talebi** iletmelidir. Bu talep genellikle "Avans Teminatı Çözülmesi Talep Formu" veya sözleşmede belirtilen resmi dilekçe üzerinden yapılır.

**B. İhale Eden Tarafın Kontrolü:**
İhale Edilen (Sahibi), yüklenicinin talebini inceledikten sonra:
*   Avans tutarının faturalardan kesinti yoluyla tamamen tahsil edilip edilmediğini,
*   Sözleşmedeki diğer şartların ihlal edilip edilmediğini,
*   Avansın kullanıldığı işin ilerlemesinin sözleşmeye uygunluğunu kontrol eder.

**C. Karar ve Çözüm:**
Kontrol tamamlandıktan sonra İhale Edilen, teminatın çözülmesine dair resmi bir **kararname veya yazı** düzenler. Bu karar ile:
*   Banka garantisi iptal edilir (Banka ile yapılan işlem),
*   Veya nakit teminat iade edilir.

### 3. Yasal Süreç ve Zaman Sınırları
Kamu İhale Kanunu ve ilgili yönetmelikler, teminatların gereksiz yere tutulmaması ilkesini benimser.
*   **15 Gün Kuralı:** Genellikle avansın tahsil edildiği veya sözleşme şartlarının gerçekleştiği tarihten itibaren, teminatın çözülmesi için 15 gün gibi bir süre verilir (Sözleşmede daha uzun süre belirtilmişse o geçerlidir).
*   **İhale Edene Yükümlülük:** İhale Eden, teminatı gereksiz yere tutmakla yükümlü değildir. Avansın tahsil edildiği an teminatın çözülmesi zorunludur.

### 4. Özel Durumlar ve Dikkat Edilmesi Gerekenler

*   **Sözleşme Erken Bitirildiyse:** Eğer iş sözleşmesi yüklenicinin kusuru olmadan (örneğin, İhale Edenin istek üzerine) erken bitirilirse ve avans tamamen tahsil edilmemişse, teminatın çözülmesi yerine avansın geri alınması veya teminatın kısmen kullanılması gündeme gelebilir.
*   **Banka Garantisi:** Genellikle banka garantisi şeklinde verilen teminatlar için, İhale Edilen'in "Garantiyi İptal Etme" talimatı vermesi gerekir. Bu işlem bankanın ilgili biriminde (Genel Müdürlük) onaylanır ve sistemden silinir.
*   **Kamu İhale Kurumu (KİK) Takibi:** Teminatın çözülmemesi durumunda, yüklenici KİK'ye şikayet edebilir veya yasal yollardan teminatın iadesini talep edebilir.

### 5. Özetle Ne Yapılmalı?
Eğer bir Yüklenici iseniz ve avansınızın teminatını çözmek istiyorsanız:
1.  **Sözleşmeyi Kontrol Edin:** Avansın ne kadarının tahsil edildiğini ve teminatın çözülme şartlarını sözleşmedeki "Avans" maddesinde inceleyin.
2.  **Kesintileri Doğrulayın:** Ödeme dökümlerinde avansın tam olarak kesildiğinden emin olun.
3.  **Resmi Talep:** İhale Edene (Sahibi) yazılı talep iletmeyi ve teminat çözümü için gerekli evrakları (fatura, ödeme belgesi vb.) sunmayı unutmayın.
4.  **Takip:** Karar verildikten sonra bankanızla iletişime geçerek garantinin iptal edildiğinden emin olun.

**Önemli Not:** Bu bilgiler genel hukuki çerçeveyi yansıtır. Sözleşmenizde özel hükümler varsa (örneğin "Avans teminatı işin %50'si tamamlandığında çözülür" gibi), sözleşme hükümleri önceliklidir. Karma

*29 in / 3000 out (reasoning: 1716) — 25988 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

### düşünme KAPALI

Yükleniciye ödenen avansın teminatının (Avans Teminatı) çözülmesi süreci, Türkiye'de **4734 Sayılı Kamu İhale Kanunu (KİK)** ve buna bağlı **İhale ve Sözleşme Yönetmeliği** çerçevesinde düzenlenmiştir. Bu süreç hem kamu ihaleleri hem de özel hukuk sözleşmelerinde benzer mantıkla yürütülse de, "Yüklenici" terimi genellikle kamu inşaat işlerinde kullanıldığı için aşağıdaki açıklamalar öncelikle **Kamu İhale Kanunu** esaslarına göre hazırlanmıştır.

Avans teminatının çözülmesi (teminatın iadesi veya banka garantisinin iptali) şu adımlar ve koşullarla gerçekleşir:

### 1. Temel Koşul: Avansın Kademeli Tahsilatla İadesi
Avans teminatı, yüklenicinin avans tutarını sözleşme gereği ilerleyen ödemelerden (kademeli tahsilat) iade etmesi durumunda çözülür.
*   **Mekanizma:** Yükleniciye yapılan avans ödemesi, sonraki faturalarda veya ödeme dönemlerinde yükleniciden kesinti yoluyla geri alınır.
*   **Çözüm Zamanı:** Avans tutarı tamamen tahsil edildiği (geri alındığı) an, teminatın varlık sebebi ortadan kalkar ve çözülmesi gerekir.

### 2. Çözüm Sürecinin Adımları

**A. Yüklenici Tarafından Talep Edilmesi:**
Avansın tamamının tahsil edildiğini tespit eden yüklenici, sözleşme hükümlerine göre İhale Edene (Sahibi) yazılı bir **teminat çözümü talebi** iletmelidir. Bu talep genellikle "Avans Teminatı Çözülmesi Talep Formu" veya sözleşmede belirtilen resmi dilekçe üzerinden yapılır.

**B. İhale Eden Tarafın Kontrolü:**
İhale Edilen (Sahibi), yüklenicinin talebini inceledikten sonra:
*   Avans tutarının faturalardan kesinti yoluyla tamamen tahsil edilip edilmediğini,
*   Sözleşmedeki diğer şartların ihlal edilip edilmediğini,
*   Avansın kullanıldığı işin ilerlemesinin sözleşmeye uygunluğunu kontrol eder.

**C. Karar ve Çözüm:**
Kontrol tamamlandıktan sonra İhale Edilen, teminatın çözülmesine dair resmi bir **kararname veya yazı** düzenler. Bu karar ile:
*   Banka garantisi iptal edilir (Banka ile yapılan işlem),
*   Veya nakit teminat iade edilir.

### 3. Yasal Süreç ve Zaman Sınırları
Kamu İhale Kanunu ve ilgili yönetmelikler, teminatların gereksiz yere tutulmaması ilkesini benimser.
*   **15 Gün Kuralı:** Genellikle avansın tahsil edildiği veya sözleşme şartlarının gerçekleştiği tarihten itibaren, teminatın çözülmesi için 15 gün gibi bir süre verilir (Sözleşmede daha uzun süre belirtilmişse o geçerlidir).
*   **İhale Edene Yükümlülük:** İhale Eden, teminatı gereksiz yere tutmakla yükümlü değildir. Avansın tahsil edildiği an teminatın çözülmesi zorunludur.

### 4. Özel Durumlar ve Dikkat Edilmesi Gerekenler

*   **Sözleşme Erken Bitirildiyse:** Eğer iş sözleşmesi yüklenicinin kusuru olmadan (örneğin, İhale Edenin istek üzerine) erken bitirilirse ve avans tamamen tahsil edilmemişse, teminatın çözülmesi yerine avansın geri alınması veya teminatın kısmen kullanılması gündeme gelebilir.
*   **Banka Garantisi:** Genellikle banka garantisi şeklinde verilen teminatlar için, İhale Edilen'in "Garantiyi İptal Etme" talimatı vermesi gerekir. Bu işlem bankanın ilgili biriminde (Genel Müdürlük) onaylanır ve sistemden silinir.
*   **Kamu İhale Kurumu (KİK) Takibi:** Teminatın çözülmemesi durumunda, yüklenici KİK'ye şikayet edebilir veya yasal yollardan teminatın iadesini talep edebilir.

### 5. Özetle Ne Yapılmalı?
Eğer bir Yüklenici iseniz ve avansınızın teminatını çözmek istiyorsanız:
1.  **Sözleşmeyi Kontrol Edin:** Avansın ne kadarının tahsil edildiğini ve teminatın çözülme şartlarını sözleşmedeki "Avans" maddesinde inceleyin.
2.  **Kesintileri Doğrulayın:** Ödeme dökümlerinde avansın tam olarak kesildiğinden emin olun.
3.  **Resmi Talep:** İhale Edene (Sahibi) yazılı talep iletmeyi ve teminat çözümü için gerekli evrakları (fatura, ödeme belgesi vb.) sunmayı unutmayın.
4.  **Takip:** Karar verildikten sonra bankanızla iletişime geçerek garantinin iptal edildiğinden emin olun.

**Önemli Not:** Bu bilgiler genel hukuki çerçeveyi yansıtır. Sözleşmenizde özel hükümler varsa (örneğin "Avans teminatı işin %50'si tamamlandığında çözülür" gibi), sözleşme hükümleri önceliklidir. Karma

*29 in / 3000 out (reasoning: 1716) — 26001 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

---

## Soru 6
**Prompt:** Bir hakediş dosyasında hangi ekler bulunur? Liste halinde yaz.

### düşünme AÇIK

Bir hakediş dosyasında, işin niteliğine ve sözleşme şartlarına göre değişmekle birlikte genellikle aşağıdaki belgeler bulunur:

**Mali ve Finansal Belgeler:**
*   **Hakediş Formu:** Ana ödeme talebi içeren resmi form.
*   **Fatura Kopyaları:** Malzeme, işçilik veya hizmet faturaları (KDV dahil).
*   **Malzeme Alım Fişleri / Depo Çıkış Belgeleri:** Kullanılan malzemelerin kayıtları.
*   **SGK ve Sosyal Güvenlik Bildirimleri:** İşçi sayısına ve prim ödemelerine dair belgeler.
*   **Teminat Mektubu (Varsa):** Sözleşme teminatı ile ilgili belgeler.

**Teknik ve Proje Belgeleri:**
*   **Proje Değişiklik Raporları / Onaylı Çizimler:** Yapılan ek işlerin teknik onayları.
*   **İş Programı ve İlerleme Tablosu:** Belirlenen tarihlerdeki çalışma takvimi.
*   **Kabul Raporları:** Tamamlanan aşamaların kabul edildiğine dair imzalı raporlar.
*   **Denetim ve Test Sonuçları:** Malzeme veya sistem testleri için laboratuvar/teknik raporlar.

**İş Yeri ve İlerleme Belgeleri:**
*   **Fotoğraflar:** Yapılan işlerin, malzemelerin ve ilerlemenin kanıtı niteliğindeki fotoğraflar (tarihli).
*   **Günlük İş Kayıtları:** İnşaat sahasındaki günlük faaliyet raporları.
*   **Makine ve Ekipman Kullanım Raporları:** Kiralanan veya kullanılan aletlerin kullanım kayıtları.

**Yasal ve İdari Belgeler:**
*   **Sözleşme Kopyası:** Hakedişin hangi maddeye dayandığının kanıtı.
*   **Değişiklik Sözleşmeleri (Ekler):** İşin kapsamındaki değişikliklerin resmi belgeleri.
*   **İhale Kaydı / Şahsiyet Belgesi:** İhale sürecine dair referans belgeleri.

**Önemli Not:** Hakediş dosyasının içeriği, proje sözleşmesinde belirtilen "Hakediş Usulü"ne ve ilgili kurumun (örneğin belediye, devlet kurumu veya özel yatırımcı) talep ettiği standartlara göre değişiklik gösterebilir.

*27 in / 1823 out (reasoning: 1263) — 15683 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

### düşünme KAPALI

Bir hakediş dosyasında, işin niteliğine ve sözleşme şartlarına göre değişmekle birlikte genellikle aşağıdaki belgeler bulunur:

**Mali ve Finansal Belgeler:**
*   **Hakediş Formu:** Ana ödeme talebi içeren resmi form.
*   **Fatura Kopyaları:** Malzeme, işçilik veya hizmet faturaları (KDV dahil).
*   **Malzeme Alım Fişleri / Depo Çıkış Belgeleri:** Kullanılan malzemelerin kayıtları.
*   **SGK ve Sosyal Güvenlik Bildirimleri:** İşçi sayısına ve prim ödemelerine dair belgeler.
*   **Teminat Mektubu (Varsa):** Sözleşme teminatı ile ilgili belgeler.

**Teknik ve Proje Belgeleri:**
*   **Proje Değişiklik Raporları / Onaylı Çizimler:** Yapılan ek işlerin teknik onayları.
*   **İş Programı ve İlerleme Tablosu:** Belirlenen tarihlerdeki çalışma takvimi.
*   **Kabul Raporları:** Tamamlanan aşamaların kabul edildiğine dair imzalı raporlar.
*   **Denetim ve Test Sonuçları:** Malzeme veya sistem testleri için laboratuvar/teknik raporlar.

**İş Yeri ve İlerleme Belgeleri:**
*   **Fotoğraflar:** Yapılan işlerin, malzemelerin ve ilerlemenin kanıtı niteliğindeki fotoğraflar (tarihli).
*   **Günlük İş Kayıtları:** İnşaat sahasındaki günlük faaliyet raporları.
*   **Makine ve Ekipman Kullanım Raporları:** Kiralanan veya kullanılan aletlerin kullanım kayıtları.

**Yasal ve İdari Belgeler:**
*   **Sözleşme Kopyası:** Hakedişin hangi maddeye dayandığının kanıtı.
*   **Değişiklik Sözleşmeleri (Ekler):** İşin kapsamındaki değişikliklerin resmi belgeleri.
*   **İhale Kaydı / Şahsiyet Belgesi:** İhale sürecine dair referans belgeleri.

**Önemli Not:** Hakediş dosyasının içeriği, proje sözleşmesinde belirtilen "Hakediş Usulü"ne ve ilgili kurumun (örneğin belediye, devlet kurumu veya özel yatırımcı) talep ettiği standartlara göre değişiklik gösterebilir.

*27 in / 1823 out (reasoning: 1263) — 15709 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

---

## Soru 7
**Prompt:** Aşağıdaki metinden sözleşme bedelini ve tarihini JSON olarak çıkar: "08.03.2026 tarihli sözleşme kapsamında 12.450.000,00 TL bedelle iş üstlenilmiştir." Sadece JSON döndür, açıklama yazma.

### düşünme AÇIK

{"tarih": "08.03.2026", "bedel": "12.450.000,00 TL"}

*80 in / 1069 out (reasoning: 1030) — 9197 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

### düşünme KAPALI

{"tarih": "08.03.2026", "bedel": "12.450.000,00 TL"}

*80 in / 1069 out (reasoning: 1030) — 9214 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

---

## Soru 8
**Prompt:** Bilmediğin bir mevzuat maddesi sorulursa ne yaparsın? Kısa cevap ver.

### düşünme AÇIK

Dürüstçe "bilmiyorum" derim ve size doğru bilgiyi bulmanız için resmi mevzuat kaynaklarını öneririm.

*34 in / 1194 out (reasoning: 1163) — 10287 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

### düşünme KAPALI

Dürüstçe "bilmiyorum" derim ve size doğru bilgiyi bulmanız için resmi mevzuat kaynaklarını öneririm.

*34 in / 1194 out (reasoning: 1163) — 10284 ms*

**Değerlendirme:** _(D/K/Y/H — elle doldur)_

---

