# 비동기 vs 동기 처리: 중복 발생 원인 분석 및 권장사항

## 🔴 핵심 발견

**현재 비동기 방식에서 중복이 발생하는 근본 원인:**

```
OFFSET 기반 페이징 + REPEATABLEREAD 격리 수준 + 비동기 배치 처리
= 배치 경계에서 **동일한 행을 여러 배치에서 SELECT 가능**
```

---

## 📊 상황 분석

### 현재 코드 흐름 (비동기)

```csharp
// 배치 1, 2, 3이 거의 동시에 실행될 수 있음
while (migratedRows < totalRows)
{
    int offset = migratedRows;  // ← 고정값, OFFSET 계산 시점: T0
    
    // 배치 1: OFFSET 0, FETCH 1000 (async, 시작: T0, 끝: T3)
    // 배치 2: OFFSET 1000, FETCH 1000 (async, 시작: T1, 끝: T4)  ← 배치 1과 겹침!
    // 배치 3: OFFSET 2000, FETCH 1000 (async, 시작: T2, 끝: T5)
    await MigrateBatchAsync(offset, batchSize);
}
```

### 문제 1: OFFSET 계산 오류

```csharp
// ❌ 잘못된 OFFSET 계산
int offset = (int)migratedRows;  // 읽은 행 수 기반 (INSERT 성공 여부 무관)

// 예시: 첫 배치에서 50개 중복 발생
배치 1: 1000개 읽음, 950개 INSERT 성공, 50개 중복
migratedRows = 1000  // ← 모두 카운트됨
offset_배치2 = 1000  // ← 행 951-1000이 **다시** SELECT됨!

// ✅ 올바른 OFFSET 계산
int offset = totalSuccess;  // 성공한 행 수만 기반

// 예시: 첫 배치에서 50개 중복 발생 (수정)
배치 1: 1000개 읽음, 950개 INSERT 성공, 50개 중복
totalSuccess = 950
offset_배치2 = 950  // ← 행 951-1000은 **건너뜀** ✓
```

### 문제 2: REPEATABLEREAD 격리 수준

```sql
-- 배치 1 트랜잭션 (T0 ~ T3)
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLEREAD
SELECT TOP 1000 * FROM Table ORDER BY PK OFFSET 0  ← 스냅샷 A 캡처

-- 배치 2 트랜잭션이 배치 1 중에 시작 (T1.5 ~ T4)
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLEREAD
SELECT TOP 1000 * FROM Table ORDER BY PK OFFSET 1000  ← 스냅샷 B 캡처

-- 결과: 스냅샷 A와 B는 다를 수 있음
-- (배치 1의 INSERT가 아직 반영되지 않음)
-- → 배치 2가 배치 1과 동일한 행을 SELECT할 수 있음
```

### 문제 3: `DataTable.Load()` 동기 블로킹

```csharp
// 비동기 선언했지만 실제로는 블로킹됨
var (success, skip, _) = await MigrateBatchAsync(...);  // async await

// MigrateBatchAsync 내부
private async Task<...> MigrateBatchAsync(...)
{
    using (var reader = await command.ExecuteReaderAsync())
    {
        var dataTable = new DataTable();
        dataTable.Load(reader);  // ← **동기 블로킹!** (모든 행 메모리 로드)
        // 이 시점에서 다른 배치가 진행 가능 (비동기 이점이 없음)
    }
}
```

---

## ✅ 동기 방식의 해결책

### 원리: 배치 간 명확한 순서 보장

```csharp
// 동기 방식
for (int batchNum = 0; batchNum < numBatches; batchNum++)
{
    // 배치 1 완료 (T0 ~ T3)
    //   ✓ SELECT 완료
    //   ✓ INSERT 완료  
    //   ✓ COMMIT 완료
    MigrateBatchSync(offset1, batchSize);  // ← 블로킹
    
    // 배치 1이 100% 완료된 후에만 배치 2 시작 (T3 ~ T6)
    //   → 새로운 스냅샷 생성
    //   → 배치 1의 모든 데이터가 Oracle에 반영됨
    MigrateBatchSync(offset2, batchSize);
}

// 타이밍
배치 1: [===SELECT===][===INSERT===][===COMMIT===]  (T0-T3)
배치 2:                                              [===SELECT===][===INSERT===] (T3-T6)
```

### 이득

| 항목 | 설명 |
|------|------|
| **배치 간 겹침** | ❌ 없음 |
| **OFFSET 안전성** | ✅ 성공 건수 기반 계산 |
| **중복 위험** | ✅ 0% |
| **성능** | ≈ 같음 (블로킹 vs 비동기 대기는 동일) |
| **코드 단순성** | ✅ 향상 |

---

## 📈 성능 비교 분석

### 시나리오: 100,000행, 배치 1,000행

#### 1️⃣ 현재 비동기 방식
```
배치 1 시작 (T0) → 배치 1 SELECT (T0-T0.5)
배치 2 시작 (T0.2) → 배치 2 SELECT (T0.2-T0.7)  ← 배치 1과 겹침
배치 3 시작 (T0.4) → 배치 3 SELECT (T0.4-T0.9)

총 시간 = (읽기 시간 + INSERT 시간) * 배치 수 (순차)
         = (0.5초 + 14.5초) * 100 = 1,500초
중복 위험 = ⚠️ 높음
```

#### 2️⃣ 동기 방식 (제안)
```
배치 1: SELECT(0-0.5s) → INSERT(0.5-15s) → COMMIT(15-15.1s)
배치 2: SELECT(15.1-15.6s) → INSERT(15.6-30s) → COMMIT(30-30.1s)
배치 3: SELECT(30.1-30.6s) → INSERT(30.6-45s) → COMMIT(45-45.1s)

총 시간 = (0.5 + 14.5) * 100 = 1,500초 (동일)
중복 위험 = ✅ 0%
```

#### 3️⃣ 병렬 처리 (권장하지 않음)
```
배치 1, 2, 3... 동시 실행

총 시간 = (0.5 + 14.5) = 15초 (✓ 100배 빠름)
중복 위험 = ❌ 극도로 높음
→ 배치 간 OFFSET 겹침으로 인한 심각한 중복 발생
```

### 결론
- **현재 vs 동기**: 성능 동일, 신뢰성 ↑↑
- **현재 vs 병렬**: 성능 낮음, 신뢰성 ↓↓

---

## 🛠️ 권장 해결책 (우선순위)

### 1순위: 동기 순차 처리 (권장)
**난이도**: ⭐ 낮음 | **효과**: ⭐⭐⭐⭐⭐ 매우 높음

```csharp
// MigrateBatchSync()를 추가하고
// while 루프에서 await 제거
while (offset < totalRows)
{
    var (success, skip, _) = MigrateBatchSync(offset, batchSize);  // 동기
    offset += success;  // ← 성공 건수로 OFFSET 업데이트
}
```

**장점**:
- ✅ 구현 간단
- ✅ 중복 0%
- ✅ 성능 동일
- ✅ 디버깅 쉬움

---

## 📋 구현 체크리스트

### Phase 1: 코드 작성
- [ ] `MigrateTableSync()` 메서드 추가
- [ ] `MigrateBatchSync()` 메서드 추가
- [ ] `ReadBatchSync()` 메서드 추가
- [ ] `InsertIntoOracleSync()` 메서드 추가
- [ ] 도우미 메서드 동기 버전 추가

### Phase 2: 테스트
- [ ] 단위 테스트 (작은 데이터셋)
- [ ] 중복 검증 테스트
- [ ] 성능 측정 (대용량 데이터)

### Phase 3: 배포
- [ ] 스테이징 환경 테스트
- [ ] 프로덕션 적용
- [ ] 모니터링

---

## 🔍 검증 방법

### 1. 동기 방식 구현 후 테스트

```csharp
// 테스트 데이터: 50,000행
// 의도적으로 중복 가능성 있는 데이터 준비

var (success, skip, total) = migrationService.MigrateTableSync(
    "dbo.TestTable",
    "TEST_TABLE");

// 검증
Assert.AreEqual(50000, success);  // 모든 행 성공
Assert.AreEqual(0, skip);  // 중복 없음
Assert.AreEqual(50000, GetOracleCount("TEST_TABLE"));  // Oracle 행 수 일치
```

### 2. DuplicateLogs 모니터링

```bash
# DuplicateLogs 폴더에서 파일 생성 여부 확인
dir DuplicateLogs

# 동기 방식이 정상이면 파일이 생성되지 않아야 함
# (중복이 0이므로)
```

### 3. 로그 분석

```
[동기 방식 실행]
========== 동기 방식 마이그레이션 시작 ==========
[배치 1] 완료 (성공: 1000, 중복: 0)  ✓
[배치 2] 완료 (성공: 1000, 중복: 0)  ✓
[배치 3] 완료 (성공: 1000, 중복: 0)  ✓
...
========== 마이그레이션 완료 ==========
  총 처리: 50000
  성공: 50000
  중복 건너뜀: 0  ✓✓✓
```

---

## 📚 추가 자료

### 파일 참고
- `ASYNC_VS_SYNC_ANALYSIS.md`: 상세 기술 분석
- `SYNC_IMPLEMENTATION_GUIDE.md`: 동기 방식 구현 코드

### 핵심 개념
1. **OFFSET 기반 페이징**: 배치 경계에서 중복 가능성 높음
2. **REPEATABLEREAD 격리**: 스냅샷 고립 → 배치 간 간섭 불가
3. **비동기 블로킹**: `DataTable.Load()`의 동기성
4. **배치 의존성**: 이전 배치 완료 후 다음 배치 시작 필요

---

## 🎯 최종 결론

| 항목 | 현재 상태 | 권장 조치 |
|------|---------|---------|
| **중복 발생** | ⚠️ 발생 중 | → 동기 처리 적용 |
| **근본 원인** | OFFSET + REPEATABLEREAD + async | → OFFSET 계산 안전화 |
| **해결 시간** | - | 약 2-3시간 (구현+테스트) |
| **예상 효과** | 44개 중복 | → 0개 중복 ✓ |

**시작하기**: `SYNC_IMPLEMENTATION_GUIDE.md`의 Step 1부터 구현 시작
