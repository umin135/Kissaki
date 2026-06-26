# KatanaEngine 파일 포맷 분석 노트

> 대상 게임: Dead or Alive 6 Last Round  
> 엔진: KatanaEngine (KOEI TECMO)  
> 분석 날짜: 2026-06

---

## 목차

1. [KTID 시스템](#1-ktid-시스템)
2. [RDB / RDX — 자산 데이터베이스](#2-rdb--rdx--자산-데이터베이스)
3. [FDATA / IDRK — 압축 컨테이너](#3-fdata--idrk--압축-컨테이너)
4. [G1T — 텍스처 파일](#4-g1t--텍스처-파일)
5. [기타 포맷 (미구현)](#5-기타-포맷-미구현)
6. [자산 접근 흐름 요약](#6-자산-접근-흐름-요약)

---

## 1. KTID 시스템

KTID(KaTana ID)는 KatanaEngine이 파일을 식별하는 데 사용하는 **32비트 해시**다.  
문자열 파일명 대신 해시값으로 자산을 참조하기 때문에 원본 파일명은 알 수 없다.

### 두 종류의 KTID

| 필드 | 역할 |
|------|------|
| **FileKtid** | 이 에셋 파일 자체의 고유 ID (에셋을 식별하는 "이름") |
| **TypeKtid** | 이 에셋의 **타입**을 나타내는 ID (확장자와 1:1 대응) |

예를 들어 `0xABCD1234`라는 FileKtid를 가진 에셋이  
TypeKtid `0xafbec60c`라면 → `.g1t` 텍스처 파일임을 의미한다.

### TypeKtid → 확장자 매핑 (확인된 것)

| TypeKtid | 확장자 | 내용 |
|----------|--------|------|
| `0x563bdef1` | `.g1m` | 3D 모델 (지오메트리) |
| `0xafbec60c` | `.g1t` | 텍스처 (GT1G) |
| `0xAD57EBBA` | `.g1t` | 텍스처 (GT1G, 대체 코드) |
| `0x6fa91671` | `.g1a` | 애니메이션 |
| `0x5153729b` | `.mtl` | 머티리얼 정의 |
| `0x27bc54b7` | `.rigbin` | 리그/스켈레톤 바이너리 |
| `0x54738c76` | `.g1co` | 충돌(Collision) |
| `0x8e39aa37` | `.ktid` | KTID 참조 목록 |
| `0x20a6a0bb` | `.kidsobjdb` | 오브젝트 DB |
| `0x56efe45c` | `.grp` | 그룹 정의 |
| `0x0d34474d` | `.srst` | 사운드 리소스 테이블 |
| `0xbbd39f2d` | `.srsa` | 사운드 리소스 아카이브 |
| `0xD7F47FB1` | `.efpl` | 이펙트 플레이리스트 |
| `0xb0a14534` | `.sgcbin` | 씬 그래프 바이너리 |
| `0x786dcd84` | `.g1n` | (용도 미파악) |
| *(그 외)* | `.bin` | 미매핑 |

DOA6 기준 78,627개 에셋이 `root.rdb`에 등록되어 있다.

---

## 2. RDB / RDX — 자산 데이터베이스

### 파일 위치

```
fdata_package/root.rdb    ← 모든 에셋의 메타데이터
fdata_package/root.rdx    ← fdata 파일 인덱스 (fdataId → 파일명 해시)
```

### RDB 헤더 (0x20 bytes)

```
[0x00] "_DRK"        magic (4B)
[0x04] version       (4B)
[0x08] header_size   u32 — 엔트리 시작 오프셋
[0x0C] system_id     u32
[0x10] file_count    u32 — 총 에셋 수
[0x14] database_id   u32
[0x18] folder_path   ASCII 8B (null-padded)
```

### RDB 엔트리 구조 (헤더 이후 연속 배치, 4바이트 정렬)

각 엔트리는 그 자체로 IDRK 헤더를 포함하는 작은 레코드:

```
[+0x00] "IDRK"           magic (4B)
[+0x04] version          (4B)
[+0x08] entry_size       u64 — 이 엔트리 전체 크기 (bytes)
[+0x10] data_size        u64 — 뒤에 붙는 위치 메타 크기 (0x0D 또는 0x11)
[+0x18] file_size        u64 — 압축 해제 후 실제 파일 크기
[+0x20] entry_type       u32
[+0x24] file_ktid        u32 — 이 에셋의 고유 ID
[+0x28] type_info_ktid   u32 — 타입 ID (위 매핑 테이블 참조)
[+0x2C] flags            u32
         bit[17]   → StorageType: 1=Internal(fdata에 있음), 0=External
         bit[20-25]→ CompressionType: 0=None, 1=Zlib, 4=ZlibExt
```

엔트리 끝(entry_start + entry_size - data_size)에 **위치 메타**가 붙는다:

**RdbLocation32** (data_size == 0x0D, 4GB 이하 파일):
```
[+0x02] fdata_offset    u32 — .fdata 파일 내 오프셋
[+0x06] size_in_cont    u32 — .fdata 내 차지하는 크기
[+0x0A] fdata_id        u16 — 어느 .fdata 파일인지 (rdx로 해석)
```

**RdbLocation40** (data_size == 0x11, 4GB 초과):
```
[+0x02] offset_high     u8  — 상위 8비트
[+0x06] offset_low      u32 — 하위 32비트 → 실제 오프셋 = (high<<32)|low
[+0x0A] size_in_cont    u32
[+0x0E] fdata_id        u16
```

### RDX 구조 (8바이트 엔트리 반복)

```
[+0x00] fdata_id     u16  — RDB 위치 메타의 fdata_id 값
[+0x02] padding      u16
[+0x04] file_hash    u32  — 파일명 해시 → "0x{file_hash:x8}.fdata"
```

즉 `fdata_id=3`이라면 rdx에서 idx==3인 항목의 file_hash를 찾아 `0x00a3f1b2.fdata` 같은 이름을 만든다.

---

## 3. FDATA / IDRK — 압축 컨테이너

### 파일 위치

```
fdata_package/0x????????.fdata    ← hex 해시 이름, 수백 개 존재 (총 ~78 GB)
```

### IDRK 블록 레이아웃

RDB로 찾은 offset/size로 .fdata 파일에서 바이트를 읽으면 IDRK 블록이 나온다:

```
[0x00] "IDRK"              magic (4B)
[0x04] version             (4B)
[0x08] total_block_size    u64 — 이 블록 전체 크기
[0x10] compressed_size     u64 — zlibext payload 크기
[0x18] uncompressed_size   u64 — 압축 해제 후 크기
[0x20] param_data_size     u32
[0x24~0x37]                (padding / 기타 필드)
[0x38~0x57]                param block 32B (고정)
[0x58+]                    ← overhead는 항상 88B (0x58)
```

### payload 위치 계산

```
payload_start = raw_block_length - compressed_size
payload_size  = compressed_size
```

> **주의**: 0x30 위치의 paramCount 필드를 읽어 계산하면 garbage가 나온다.  
> 항상 "끝에서 compressed_size만큼"이 payload다 (overhead = 88B 고정).

### 압축 방식 (ZlibExt)

payload는 **zlibext** 형식으로, 내부적으로 표준 zlib deflate를 여러 청크로 나눈 것:

```
[청크 헤더] uncompressed_size u32 | compressed_size u32
[청크 데이터] zlib deflate stream
... 반복 ...
```

각 청크를 순서대로 `DeflateStream`으로 해제하고 이어붙이면 원본 파일이 완성된다.

---

## 4. G1T — 텍스처 파일

IDRK에서 압축 해제한 결과가 `.g1t` 파일이면 GT1G 포맷이다.

### GT1G 헤더

```
[0x00] "GT1G"          magic (4B)
[0x04] version         ASCII 4B ("0600" 또는 "1600")
[0x08] file_size       u32
[0x0C] header_size     u32 = table_base (텍스처 오프셋 테이블 시작 위치)
[0x10] platform        u32
[0x14] tex_count       u32 — 텍스처(슬롯) 수
```

### 텍스처 오프셋 테이블

`table_base`부터 `tex_count * 4` 바이트 = 각 슬롯의 상대 오프셋 배열:

```
[table_base + i*4] offset_i    u32 — 슬롯 i의 헤더 시작위치 (table_base 기준)
```

즉 슬롯 i의 실제 위치: `ep = table_base + offset_table[i]`

### 슬롯 헤더 (ep 위치)

```
[ep+0] byte0      = (mip_count << 4) | flags
[ep+1] fmt_code   u8  — 텍스처 포맷 (아래 표 참조)
[ep+2] dim        u8  = (log2_width << 4) | log2_height
[ep+3] 기타 flags
[ep+4~7]          (예약 또는 추가 정보)
[ep+8] ext_size   u32 — 슬롯 헤더 뒤에 붙는 확장 데이터 크기
```

픽셀 데이터 시작: `ep + 8 + ext_size`

크기 계산: `w = 1 << ((dim >> 4) & 0xF)`, `h = 1 << (dim & 0xF)`

mip_count == 0이면 1로 처리.

### 텍스처 포맷 코드

| fmt_code | 포맷 | 블록 크기 | 설명 |
|----------|------|-----------|------|
| `0x01` | RGBA8_UNORM | 4 B/px | 비압축 32비트 |
| `0x02` | BGRA8_UNORM | 4 B/px | 비압축 (채널 순서 주의) |
| `0x04` | R8_UNORM | 1 B/px | 그레이스케일 |
| `0x3C` | BC1_SRGB | 8 B/블록 | BC1 (sRGB 감마) |
| `0x3D` | BC2_SRGB | 16 B/블록 | BC2 |
| `0x3E` | BC3_SRGB | 16 B/블록 | BC3 |
| `0x59` | BC1_UNORM | 8 B/블록 | BC1 (알파없는 색상) |
| `0x5A` | BC2_UNORM | 16 B/블록 | BC2 |
| `0x5B` | BC3_UNORM | 16 B/블록 | BC3 |
| `0x5C` | BC4_UNORM | 8 B/블록 | 단채널 → 그레이스케일로 표시 |
| `0x5D` | BC5_UNORM | 16 B/블록 | 2채널 (RG) → 노말맵 Z 재구성 |
| `0x5E` | BC6H_UF16 | 16 B/블록 | HDR (half-float), Reinhard 톤매핑 필요 |
| `0x5F` | BC7_UNORM | 16 B/블록 | 고품질 색상 / 알파 |

BCn 블록 크기 공식:
```
block_count = ceil(w/4) * ceil(h/4)
pixel_data_size = block_count * bytes_per_block
```

### BC6H 처리 (HDR → LDR)

BC6H는 half-float 값을 담기 때문에 일반 `[0,255]` 변환이 불가능하다.  
Reinhard 톤매핑으로 LDR로 변환:
```
out = channel / (1 + channel) * 255
```

### 슬롯 구조 정리

하나의 .g1t 파일에는 여러 슬롯이 존재할 수 있다. DOA6 캐릭터 예시:

| 슬롯 | 용도 | 일반적인 포맷 |
|------|------|--------------|
| 0 | 베이스 컬러 (Albedo) | BC7, BC1 |
| 1 | 노말맵 | BC5 |
| 2 | 메탈릭/러프니스/AO | BC7, BC1 |
| 3 | 이미시브 또는 마스크 | BC6H, BC7 |

*(슬롯 배치는 머티리얼 정의 `.mtl`에 따라 달라질 수 있음)*

---

## 5. 기타 포맷 (미구현)

### G1M — 3D 모델

TypeKtid `0x563bdef1`, 확장자 `.g1m`  
버텍스 버퍼, 인덱스 버퍼, 본(Bone) 테이블 등을 포함.  
KatanaEngine의 핵심 포맷으로 구조가 복잡하다. 커뮤니티 도구(noesis 플러그인 등) 참고 필요.

### G1A — 애니메이션

TypeKtid `0x6fa91671`, 확장자 `.g1a`  
G1M의 스켈레톤을 참조하는 키프레임 데이터.

### MTL — 머티리얼

TypeKtid `0x5153729b`, 확장자 `.mtl`  
어떤 KTID의 G1T 텍스처를 어느 슬롯에 바인딩할지 정의.  
G1T 파일과 연결고리이므로 텍스처 슬롯 의미를 파악하려면 이 파일 파싱이 필요.

### SRST / SRSA — 사운드

TypeKtid `0x0d34474d` / `0xbbd39f2d`  
사운드 리소스 테이블 + 아카이브. 포맷 미분석.

### K300 / LFMO

| 시그니처 | 용도 |
|----------|------|
| `K300` (`.lnk`) | 아카이브 인덱스 링크 |
| `LFMO` (`.bin`) | 로드 순서 메타데이터 |

둘 다 `fdata_package/` 루트에 별도로 존재하며 구체적 구조 미분석.

---

## 6. 자산 접근 흐름 요약

```
root.rdb + root.rdx
   │
   ├─ 헤더: file_count = 78,627
   │
   └─ 엔트리 [i]:
       ├─ file_ktid    → 에셋 식별자 (현재 뷰어에 표시되는 hex 이름)
       ├─ type_ktid    → 확장자 결정 (.g1t / .g1m / ...)
       ├─ file_size    → 압축 해제 후 크기
       ├─ fdata_id + rdx → "0x????????.fdata" 파일명 결정
       ├─ fdata_offset → .fdata 내 IDRK 블록 시작 위치
       └─ size_in_cont → IDRK 블록 크기
            │
            ▼
    .fdata 파일 [fdata_offset .. fdata_offset+size_in_cont]
       │
       └─ IDRK 블록:
           ├─ compressed_size, uncompressed_size
           ├─ payload = 끝에서 compressed_size 바이트
           └─ ZlibExt 해제 → 원본 파일
                │
                ▼
            .g1t: GT1G 헤더 → 슬롯별 픽셀 데이터 → BCn 디코딩 → 이미지
            .g1m: G1M 모델 데이터 (미구현)
            .mtl: 머티리얼 (미구현)
            ...
```

---

## 미해결 / 추가 조사 필요

- [ ] `file_ktid`가 원본 파일명의 해시인지, 아니면 순차 할당인지 확인
- [ ] `G1M` 포맷 구조 (버텍스 레이아웃, 본 테이블)
- [ ] `MTL` 파싱 → G1T 슬롯-용도 매핑
- [ ] 슬롯 fmt_code `0x00`인 경우의 의미 (빈 슬롯 vs. 다른 인코딩)
- [ ] ext_size > 0x0C인 경우 확장 데이터 구조
- [ ] BC6S (signed) 포맷 실제 사용 여부
