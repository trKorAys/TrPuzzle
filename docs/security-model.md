# Güvenlik modeli

## Değişmezler

1. Yalnızca assembly içine derlenmiş manifest hedefleri aranabilir.
2. Serbest adres, HASH160, public key, WIF, seed phrase veya cüzdan içe aktarma yüzeyi yoktur.
3. Her iş aralığı seçilen onaylı puzzle aralığının alt kümesidir.
4. Native/GPU sonucu güvenilir kabul edilmez; CPU üzerinde yeniden hesaplanır.
5. Private key konsola, telemetriye, normal loga veya exception mesajına yazılmaz.
6. Sonuç yalnızca parola korumalı ayrı kasaya yazılır ve mevcut kasa sessizce ezilmez.
7. İlk sürüm işlem oluşturmaz, imzalamaz, yayınlamaz ve otomatik transfer yapmaz.
8. Bir aday doğrulandığında supervisor bütün worker'ları ortak cancellation ile durdurmalıdır.

## Aday bulunduğunda işlem akışı

1. GPU/CPU worker yalnızca aday byte dizisini döndürür; aday `CandidateVerifier` ile bağımsız CPU
   doğrulamasından geçer.
2. Doğrulama başarılıysa sonuç, mevcut dosyanın üzerine yazılmadan AES-GCM/PBKDF2 ile şifreli
   vault dosyasına atomik olarak kaydedilir.
3. Oturum `Found` durumuna alınır ve supervisor yeni iş paketi istemeyi bırakır. Private key
   konsola, dashboard'a veya normal loglara yazılmaz; yalnızca kısa ömürlü buffer temizlenir.
4. İlk sürümde vault açma, transfer oluşturma, imzalama veya broadcast yoktur. Operatör sonucu
   ayrı ve kontrollü bir offline prosedürle incelemelidir.

## Tehdit sınırı

Bu kontroller, normal uygulama/CLI kullanımında keyfi hedef aramasını engeller. Aynı süreç içinde kötü
niyetli kod çalıştırılması, değiştirilmiş binary veya kaynak kodun yeniden derlenmesi bu sınırın
dışındadır. Release artefact'ları için ileride code signing ve manifest provenance imzası eklenmelidir.

## Hassas veri yaşam döngüsü

- Private key byte dizisi yalnızca doğrulanmış `SearchResult` içinde kısa süre tutulur.
- `Dispose` çağrısı anahtar buffer'ını `CryptographicOperations.ZeroMemory` ile temizler.
- Sonuç kasasının açık metin payload'ı ve parola UTF-8 buffer'ı yazımdan sonra temizlenir.
- Kasa parolası komut satırı parametresi olmamalıdır; sonraki servis aşamasında maskeli interaktif
  giriş veya işletim sistemi secret store kullanılmalıdır.

## Henüz tamamlanmayan kontroller

- birden fazla process worker'ını durduran merkezi cancellation/lease koordinasyonu;
- imzalı release manifestleri;
- CUDA watchdog, cihaz sağlık kontrolleri ve hata enjeksiyon testleri;
- parola kaybına karşı vault kurtarma mekanizması (parola olmadan kurtarma desteklenmez) ve işletim sistemi dosya izinlerinin sıkılaştırılması.
