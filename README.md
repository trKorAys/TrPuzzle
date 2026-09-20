# TrPuzzle

TrPuzzle, yalnızca uygulamayla birlikte derlenmiş onaylı kamuya açık Bitcoin Puzzle hedefleri ve yerleşik test vektörleri üzerinde çalışan CPU/CUDA arama motorudur.

> **Güvenlik sınırı:** Uygulama rastgele Bitcoin adresi, HASH160 hedefi, public key, WIF, seed phrase veya kullanıcı cüzdanı kabul etmez. Çalışma zamanında dışarıdan hedef/manifest yüklenemez. Yalnızca gömülü `ApprovedPuzzle` kataloğundaki hedefler kullanılabilir.

## Projenin durumu

İlk sürüm aşağıdaki bileşenleri içerir:

- unsigned ve sabit genişlikli `UInt256` anahtar aralıkları;
- gömülü kamuya açık puzzle kataloğu (`btc-puzzle-1` … `btc-puzzle-160`);
- sonucu bilinen CPU/CUDA test vektörleri;
- CPU referans doğrulaması ve native adaylar için bağımsız doğrulama;
- atomik lease/checkpoint/resume akışı ve SQLite audit kayıtları;
- CUDA cihaz keşfi, self-test, benchmark ve secp256k1/HASH160 arama kernel’i;
- sabit konsol dashboard’ı;
- AES-256-GCM + PBKDF2-SHA256 ile şifreli sonuç vault’ı.

MSSQL, dağıtık Coordinator, çoklu GPU telemetrisi, otomatik transfer/işlem yayınlama ve keyfi hedef arama bu sürümün kapsamı dışındadır.

## Gereksinimler

Şu anki CUDA yolu Windows x64 içindir.

- .NET SDK 10
- NVIDIA sürücüsü ve CUDA Toolkit (varsayılan yol: `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.1`)
- CUDA native derlemesi için Visual Studio 2022 C++ Build Tools
- CUDA destekli NVIDIA GPU
- PowerShell 5+ veya PowerShell 7

`CUDA_PATH` tanımlıysa native derleme script’i bu değişkeni kullanır. `scripts/build-native-cuda.ps1` içindeki `-arch=sm_86` değeri RTX 3060 test makinesine göre ayarlıdır; farklı GPU’larda uygun compute capability ile değiştirilmelidir.

## Kurulum ve ilk doğrulama

```powershell
git clone <repository-url>
cd TrPuzzle

dotnet restore TrPuzzle.sln
dotnet build TrPuzzle.sln --no-restore --nologo

# CUDA DLL’sini oluşturur
powershell -NoProfile -ExecutionPolicy Bypass `
  -File scripts/build-native-cuda.ps1

# Yerleşik CPU/CUDA ve model testleri
dotnet run --project tests/TrPuzzle.Core.Tests --no-build
dotnet run --project tests/TrPuzzle.Engine.Tests --no-build
dotnet run --project tests/TrPuzzle.Integration.Tests --no-build
dotnet run --project src/TrPuzzle.Service --no-build -- self-test

# CUDA cihazı ve ABI kontrolü
dotnet run --project src/TrPuzzle.Service --no-build -- `
  devices --native .build/native-cuda-manual/trpuzzle_native.dll
dotnet run --project src/TrPuzzle.Service --no-build -- `
  gpu-self-test --device 0 `
  --native .build/native-cuda-manual/trpuzzle_native.dll
```

Native DLL ile .NET servisinin aynı kaynak sürümünden derlenmiş olması gerekir. `Native ABI ... is incompatible` hatası görülürse çalışan eski TrPuzzle süreçlerini kapatıp native DLL ve solution’ı birlikte yeniden derleyin.

## Önerilen kullanım: etkileşimli GPU taraması

En kolay yol:

```powershell
.\scripts\start-gpu-scan.bat
```

Program yalnızca gömülü onaylı puzzle listesinden numara seçtirir. `TRPUZZLE_VAULT_PASSWORD` yoksa parola gizli olarak iki kez sorulur; parola komut satırında verilmez. Tarama veya `REVEAL` tamamlandıktan sonra başlatıcı kapanmaz; puzzle seçimine döner. Pencere yalnızca menüden `0 - Exit` seçildiğinde kapanır.

Varsayılanlar:

- SQLite: `checkpoints\<puzzle>-chunk65536-filter3.db`
- vault: `checkpoints\<puzzle>.vault`
- work package (`Chunk`): `65.536` (`16^4`, son 4 hex hanesi)
- atomik checkpoint: `4.096` anahtar
- hex ardışık karakter filtresi: `3`
- CUDA cihazı: `0`

Örnek:

```powershell
.\scripts\start-gpu-scan.bat `
  -Chunk 65536 `
  -Checkpoint 65536 `
  -MaxHexRun 3 `
  -Database "checkpoints\btc-puzzle-71-chunk65536-filter3.db"
```

`MaxHexRun 3`, private-key hex gösteriminin anlamlı kısmında `1111` gibi dört aynı karakterin art arda gelmesini engeller; `111` kabul edilir. Baştaki sabit sıfırlar hesaba katılmaz. Bu filtre **tam kapsamlı bir arama değildir**. Puzzle aralığının tamamını taramak için ayrı bir veritabanıyla `-MaxHexRun 0` kullanın:

```powershell
.\scripts\start-gpu-scan.bat `
  -MaxHexRun 0 `
  -Database "checkpoints\btc-puzzle-71-full.db"
```

Filtre profilleri SQLite içinde saklanır; aynı veritabanında filtreli ve filtresiz profiller karıştırılmaz.

## Dashboard ve kontrol komutları

Tarama dashboard’ı seçilen puzzle adresini, onaylı toplam aralığı, aktif aralığı, aktif ilerlemeyi, paket sayısını, toplam kontrol edilen anahtarı, hızı, çalışma süresini, GPU sıcaklığını ve vault durumunu gösterir. Sıcaklık okunamazsa `N/A` gösterilir.

Doğrudan servis komutları:

```powershell
dotnet run --project src/TrPuzzle.Service --no-build -- list
dotnet run --project src/TrPuzzle.Service --no-build -- status --puzzle btc-puzzle-71 --db checkpoints\btc-puzzle-71-full.db
dotnet run --project src/TrPuzzle.Service --no-build -- pause --puzzle btc-puzzle-71 --db checkpoints\btc-puzzle-71-full.db
dotnet run --project src/TrPuzzle.Service --no-build -- resume --puzzle btc-puzzle-71 --db checkpoints\btc-puzzle-71-full.db
dotnet run --project src/TrPuzzle.Service --no-build -- rescan --puzzle btc-puzzle-71 --db checkpoints\btc-puzzle-71-full.db
```

`rescan`, tamamlanmış bir oturum için yeni deterministik audit epoch’u başlatır. Crash veya Ctrl+C sonrası aynı checkpoint veritabanı kullanılarak güvenli noktadan devam edilir.

## Benchmark ve GPU self-test

Gerçek bir puzzle taramasından önce test vektörü ile doğrulama önerilir:

```powershell
$env:TRPUZZLE_VAULT_PASSWORD = 'en-az-12-karakterli-guvenli-parola'

dotnet run --project src/TrPuzzle.Service --no-build -- `
  gpu-benchmark --device 0 --puzzle test-scalar-16 --iterations 5 `
  --native .build/native-cuda-manual/trpuzzle_native.dll

dotnet run --project src/TrPuzzle.Service --no-build -- `
  gpu-self-test --device 0 `
  --native .build/native-cuda-manual/trpuzzle_native.dll
```

Benchmark kısa test aralıklarında SQLite yazma maliyetini ölçmez; karşılaştırma aynı GPU, sürücü, CUDA ve build ayarlarıyla yapılmalıdır.

## Vault ve bulunan aday

Bir aday bulunduğunda:

1. GPU/CPU sonucu bağımsız CPU secp256k1 + HASH160 doğrulamasından geçer.
2. Tüm worker’lar durdurulur.
3. Sonuç atomik olarak şifreli vault’a yazılır.
4. Oturum `Found` durumuna geçer.

Private key normal loglara veya dashboard’a yazılmaz. Açıkça istemeden vault açılmaz. Etkileşimli başlatıcı mevcut vault bulursa taramaya devam etme veya bir kez reveal etme seçeneği sunar. Doğrudan inceleme için:

```powershell
$env:TRPUZZLE_VAULT_PASSWORD = 'mevcut-vault-parolasi'
dotnet run --project src/TrPuzzle.Service --no-build -- `
  vault-info --puzzle btc-puzzle-71 --vault checkpoints\btc-puzzle-71.vault

dotnet run --project src/TrPuzzle.Service --no-build -- `
  vault-reveal --puzzle btc-puzzle-71 --vault checkpoints\btc-puzzle-71.vault
```

`vault-reveal` ayrıca `REVEAL` onayı ister. Private key’i sohbete, issue’ya, loga veya Git’e koymayın. Otomatik transfer, imzalama ve işlem yayınlama yoktur.

## Veri ve güvenlik politikası

- `--address`, `--target`, `--wif` ve seed phrase seçenekleri yoktur.
- Puzzle hedefleri build içine gömülüdür; çalışma zamanında URL veya dosyadan manifest alınmaz.
- Her iş paketi onaylı aralığın alt kümesidir ve SQLite audit kayıtlarına yazılır.
- Native aday hiçbir zaman tek başına kabul edilmez; CPU yeniden doğrulaması zorunludur.
- `checkpoints/*.db` ve `checkpoints/*.vault` `.gitignore` içindedir. Bu dosyaları public repository’ye göndermeyin.
- Kayıtların çözülmesi için kullanılan vault parolası TrPuzzle tarafından saklanmaz; kaybedilirse vault kurtarılamaz.

Bu proje yalnızca kamuya açık puzzle/challenge araştırması için tasarlanmıştır. Gerçek kullanıcı cüzdanlarını veya rastgele adresleri hedeflemek desteklenmez ve uygulamanın güvenlik sınırlarının dışındadır.

## GitHub’a yayınlamadan önce

İlk push’tan önce çalışma ağacını kontrol edin. `.gitignore` veritabanı, vault ve build çıktısını dışarıda bırakır; yine de zorla ekleme yapmayın:

```powershell
git status --short
git check-ignore -v checkpoints\*.db checkpoints\*.vault .build\*
git add -n .
```

Gerçek vault/parola, private key, crash dump veya yerel makine bilgisi repository’ye gönderilmemelidir. `<repository-url>` yer tutucusunu kendi GitHub adresinizle değiştirin.

## Proje yapısı

- `TrPuzzle.Core`: puzzle modeli, `UInt256`, aralıklar ve gömülü katalog.
- `TrPuzzle.Engine`: CPU referans araması, supervisor, checkpoint ve vault.
- `TrPuzzle.Native`: C++/CUDA motoru.
- `TrPuzzle.NativeBridge`: sürümlü ve dar native ABI.
- `TrPuzzle.Service`: konsol komutları ve dashboard.
- `TrPuzzle.Coordinator`: ilerideki dağıtık çalışma için kapalı yer tutucu.
- `tests`: Core, Engine ve Integration testleri.
- `docs`: mimari, güvenlik modeli ve puzzle kaynak politikası.

Detaylar için [mimari](docs/architecture.md), [güvenlik modeli](docs/security-model.md) ve [puzzle kaynak politikası](docs/puzzle-sources.md) belgelerine bakın.

## Planlanan/opsiyonel işler

İlk sürüm için zorunlu bir iş kalmamıştır. İhtiyaca göre sonraki çalışmalar:

1. farklı NVIDIA compute capability’leri için otomatik CUDA build seçimi;
2. imzalı release artefact’ları ve manifest provenance doğrulaması;
3. CUDA watchdog/cihaz sağlık kontrolleri ve hata enjeksiyon testleri;
4. benchmark sonuçlarına göre kernel optimizasyonu;
5. ancak birden fazla makine gerektiğinde web servisli Coordinator/MSSQL.

MSSQL ve dağıtık koordinasyon tek bilgisayarlı kullanımda SQLite’ın sağladığı dayanıklılık ve audit özelliklerine göre gereksiz ek karmaşıklıktır.
