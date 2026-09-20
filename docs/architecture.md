# Mimari

## Güvenli veri akışı

```text
Gömülü onaylı manifest
          |
          v
   ApprovedPuzzleCatalog
          |
          v
      WorkRange  ---- sınır kontrolü ----+
          |                              |
          +--> CPU referans worker       |
          |                              |
                    +--> CUDA worker                 --+
                         |
                         v
                    Aday UInt256
                         |
                         v
              Bağımsız CPU doğrulaması
                         |
                         v
              Şifreli sonuç kasası
```

`ApprovedPuzzle` kurucusu assembly dışına kapalıdır. `CpuReferenceSearcher` ve native bridge ham
adres/HASH160 yerine bu nesneyi ister. `WorkRange.EnsureWithin` her iş paketinin manifestteki onaylı
aralığın içinde kalmasını sağlar.

## Katmanlar

### Core

`UInt256` dört adet `ulong` alanıyla saklanır. Hex ve `BigInteger` dönüşümleri açıkça unsigned ve
big-endian yapılır. Bu, yüksek biti 1 olan değerlerin negatif yorumlanmasını önler.

Manifestler `TrPuzzle.Core` assembly'sine embedded resource olarak derlenir. Uygulama çalışma
zamanında dosya yolu veya URL üzerinden manifest kabul etmez.

### Engine

CPU yolu sıkıştırılmış secp256k1 public key üretir ve `RIPEMD160(SHA256(publicKey))` değerini sabit
zamanlı karşılaştırmayla kontrol eder. Bulunan private key normal string modeline dönüştürülmez;
`SearchResult.Dispose` bellekteki anahtar byte'larını sıfırlar.

Sonuç kasası yeni bir dosyaya AES-256-GCM ile yazılır. Anahtar PBKDF2-SHA256 ile üretilir; geçici
dosya diske flush edildikten sonra aynı dizinde atomik olarak taşınır ve mevcut kasa ezilmez.

### Native ve NativeBridge

Native proje sürümlü ABI, gerçek CUDA cihaz keşfi, 256-bit increment self-test ve secp256k1/HASH160
range search kernel’i içerir. CUDA worker yalnızca atanmış
başlangıç/bitiş değerlerini tarayacaktır. Native katmanın bulduğu
aday, `NativeSearchCoordinator` tarafından aralık kontrolü ve bağımsız CPU kriptografik doğrulama
tamamlanmadan kabul edilmez.

### Checkpoint

`SqliteCheckpointStore`, WAL ve `synchronous=FULL` ile yerel dayanıklılık sağlar. Yeni paketler
`next_unassigned_key` imlecinden lease transaction'ı içinde üretildiği için büyük puzzle aralıkları
baştan milyonlarca satıra açılmaz. `WorkPackages` yalnızca oluşturulmuş paketleri kaydeder.

Yeni session'larda bu cursor, epoch seed'iyle deterministik bir chunk permütasyonuna dönüşür. Her
epoch aynı chunk'ı bir kez planlar; `rescan` yeni seed ve epoch başlatır, önceki epoch'un paket ve
audit kayıtları korunur. Eski checkpoint veritabanları migration sonrasında seed boş olduğu için
mevcut sıralı davranışla devam eder.

Her paket için inclusive range, `next_key`, checked counter, worker lease süresi ve hız tutulur.
`AuditEvents` lease, checkpoint, release ve completion olaylarını kaydeder. Checkpoint transaction'ı
lease sahibi, monotonik ilerleme ve manifest fingerprint kontrolü yapar.

Depolama `ICheckpointStore` ile ayrıldığı için ilk sürümde SQLite, Coordinator aşamasında ise aynı
sözleşmenin MSSQL uygulaması kullanılabilir. MSSQL'e geçiş ancak birden fazla makinenin aynı kuyruğu
paylaşması, merkezi operasyon/backup ve yüksek eşzamanlı lease ihtiyacı oluştuğunda yapılmalıdır.
