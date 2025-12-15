# 비동기(Async) vs 동기(Sync) 처리 방식 분석

## 📋 현재 코드 구조 분석

### 1. 비동기 방식 사용 부분
```
Program.cs (메인)
    ↓ await
MigrateTableAsync (테이블 단위 마이그레이션)
    ├─ MigrateBatchAsync (배치 단위, await 루프)
    │   ├─ OpenAsync()
    │   ├─ ExecuteReaderAsync()
    │   ├─ dataTable.Load(reader) ← **동기 작업!**
    │   ├─ InsertIntoOracleAsync (배치별 INSERT)
    │   └─ CommitAsync()
    └─ 루프: while(migratedRows < totalRows) { await MigrateBatchAsync() }
```

### 2. 중요한 발견: **혼합 모드 처리**
- **비동기**: SQL Server 데이터 읽기, Oracle INSERT, 트랜잭션 관리
- **동기**: `DataTable.Load(reader)` ← **매우 중요!**

---

## 🔍 중복이 발생하는 이유 (비동기 방식의 문제점)

### 문제 1: 배치 경계에서의 경합 조건(Race Condition)

```csharp
// 현재 코드 (비동기)
for (int batchNum = 0; batchNum < numBatches; batchNum++)
{
    // 배치 1: OFFSET 0, ROWS 1000 (async)
    // 배치 2: OFFSET 1000, ROWS 1000 (async, 동시 진행 가능)
    var (success, skip, total) = await MigrateBatchAsync(offset, batchSize);
}
```

**시나리오:**
1. **배치 1**: `SELECT TOP 1000 FROM table WITH (REPEATABLEREAD) ORDER BY PK OFFSET 0`
   - SQL Server: 스냅샷 A 생성 (트랜잭션 시작)
   - 행 1-1000 읽음 (시간: T0 ~ T1)
   - INSERT 진행 중 (시간: T1 ~ T3)

2. **배치 2**: 배치 1의 INSERT 중에 시작 가능
   - SQL Server: 스냅샷 B 생성 (새로운 REPEATABLEREAD 트랜잭션)
   - 행 1001-2000 읽음 (시간: T1.5 ~ T2.5)
   - INSERT 진행 (시간: T2.5 ~ T4)

**문제**: OFFSET 계산이 **미리 계산됨**
```csharp
int offset = (int)migratedRows;  // ← 이전 배치의 크기 기반
// 실제로는 INSERT 성공 건수가 아닌 **읽음 건수** 기반 OFFSET!
```

### 문제 2: `DataTable.Load()` 블로킹

```csharp
using (var reader = await command.ExecuteReaderAsync())
{
    var dataTable = new DataTable();
    dataTable.Load(reader);  // ← **동기 작업, 스레드 블로킹**
}
```

**비동기 방식의 의미 없음:**
- `ExecuteReaderAsync()`는 데이터 읽기 **명령만** 비동기
- `dataTable.Load(reader)`는 모든 행을 **동기적으로 메모리에 로드**
- 따라서 비동기의 이점이 거의 없음

### 문제 3: OFFSET 기반 페이징의 근본적 문제

```csharp
// SQL Server의 실제 동작
SELECT * FROM table 
WHERE NOT EXISTS (이미 삽입된 행)  // ← 이 조건이 없음!
ORDER BY PK 
OFFSET @offset ROWS 
FETCH NEXT @batchSize ROWS ONLY

// 결과: 
// 배치 1이 INSERT 중에도 배치 2가 같은 행들을 READ할 수 있음
```

---

## ✅ 동기(Sync) 방식이 더 안전한 이유

### 1. 배치 간 명확한 순서 보장

```csharp
// 동기 방식
for (int batchNum = 0; batchNum < numBatches; batchNum++)
{
    // 배치 1 완료 ✓
    //   - SELECT 완료
    //   - INSERT 완료
    //   - COMMIT 완료
    MigrateBatch(offset, batchSize);  // ← 동기, 블로킹
    
    // 배치 1이 100% 완료된 후에만 배치 2 시작
    //   - 새로운 스냅샷 생성
    //   - 이미 INSERT된 행은 보이지 않거나 일관성 있음
    MigrateBatch(offset + batchSize, batchSize);
}
```

**타이밍:**
- 배치 1: `[========INSERT========]` (완료)
- 배치 2: `                      [========INSERT========]` (완료)
- **겹치지 않음!**

### 2. INSERT 완료 후 OFFSET 계산

```csharp
// 동기 방식 (제안)
int totalMigrated = 0;
while (totalMigrated < totalRows)
{
    int (successCount, skipCount, _) = MigrateBatch(totalMigrated, batchSize);
    
    // ✓ INSERT 완료를 100% 보장한 후 OFFSET 업데이트
    totalMigrated += successCount;  // ← 성공 건수 기반!
    
    // 다음 배치는 INSERT된 행을 건너뜀
}
```

### 3. 트랜잭션 격리 수준의 명확한 의미

```csharp
// 동기 방식
using (var transaction = connection.BeginTransaction())
{
    // 배치 1 트랜잭션: T0 ~ T10 (완료 및 COMMIT)
    InsertIntoOracle(batch1);
    transaction.Commit();
    
    // 새로운 연결 또는 트랜잭션
    using (var transaction2 = connection.BeginTransaction())
    {
        // 배치 2 트랜잭션: T10 ~ T20
        // T10 시점에 배치 1의 모든 데이터가 Oracle에 반영됨
        InsertIntoOracle(batch2);
        transaction2.Commit();
    }
}
```

---

## 📊 성능 비교

### 비동기 방식 (현재)
```
총 소요시간: T_total = (T_read1 + T_insert1) + (T_read2 + T_insert2) + ...
모든 배치가 **순차적으로** 실행되므로 성능 개선 없음
(비동기의 이점을 못 활용 - dataTable.Load가 블로킹)

예시: 10개 배치, 각 15초
→ 150초 소요
```

### 동기 방식 (제안)
```
총 소요시간: T_total = (T_read1 + T_insert1) + (T_read2 + T_insert2) + ...
비동기와 동일한 시간

하지만 **데이터 일관성**: ✓ 완벽

예시: 10개 배치, 각 15초  
→ 150초 소요 (성능은 같음, 신뢰성은 훨씬 높음)
```

### 완전 비동기 방식 (병렬 처리)
```
총 소요시간: T_total = Max(T_read, T_insert) * numBatches
⚠️ 중복 위험 증가 (순서 보장 불가)

예시: 10개 배치 병렬 처리
→ ~15초 소요 (10배 빠르지만 중복 발생!)
```

---

## 🛠️ 권장 솔루션

### 방안 1: 동기화된 순차 처리 (권장)

```csharp
public void MigrateTableSync(string sourceTable, string targetTable)
{
    long totalRows = GetRowCount(sourceTable);
    long migratedRows = 0;
    int totalSuccess = 0;

    while (migratedRows < totalRows)
    {
        // ✓ 동기 메서드 호출
        var (successCount, skipCount, _) = MigrateBatchSync(
            sourceTable, 
            targetTable, 
            migratedRows,  // ← 이전 배치의 **성공 건수** 기반
            _batchSize
        );

        totalSuccess += successCount;
        // ✓ 성공 건수로만 OFFSET 업데이트
        migratedRows += successCount;
        
        _logger.LogInformation($"배치 완료: 누적 성공 {totalSuccess}");
    }
}

private (int successCount, int skipCount, int totalCount) MigrateBatchSync(
    string sourceTable, 
    string targetTable, 
    long offset, 
    int batchSize)
{
    // SQL Server에서 데이터 읽기 (동기)
    var dataTable = ReadBatchSync(sourceTable, offset, batchSize);
    
    if (dataTable.Rows.Count == 0)
        return (0, 0, 0);
    
    // Oracle에 INSERT (동기)
    var (success, skip) = InsertIntoOracleSync(targetTable, dataTable);
    
    return (success, skip, dataTable.Rows.Count);
}
```

### 방안 2: 명확한 배치 표식 (현재 비동기 구조 유지)

```csharp
// 각 배치에 고유 ID 부여
class BatchMetadata
{
    public int BatchNumber { get; set; }
    public long StartOffset { get; set; }
    public int SuccessCount { get; set; }
    public int SkipCount { get; set; }
}

// 배치 간 의존성 명시
while (migratedRows < totalRows)
{
    batchNumber++;
    
    // ✓ 이전 배치의 INSERT 완료 대기
    var (successCount, skipCount, _) = await MigrateBatchAsync(
        sourceTable, 
        targetTable, 
        // OFFSET을 성공 건수로 계산
        offset: migratedRows,  
        batchSize: _batchSize,
        previousBatchCompleted: true  // ← 명시적 의존성
    );
    
    // ✓ 성공 건수로만 다음 OFFSET 계산
    migratedRows += successCount;
}
```

### 방안 3: 데이터베이스 커서 기반 (가장 안전)

```sql
-- SQL Server: 커서로 배치 처리 (완벽한 순서 보장)
DECLARE @Cursor CURSOR;
DECLARE @BatchStart INT = 0;

SET @Cursor = CURSOR FORWARD_ONLY READ_ONLY
FOR SELECT * FROM SourceTable ORDER BY PK;

OPEN @Cursor;

FETCH NEXT FROM @Cursor INTO ...
WHILE @@FETCH_STATUS = 0
BEGIN
    -- 배치 처리
    -- 100개씩 INSERT
    
    FETCH NEXT FROM @Cursor INTO ...
END

CLOSE @Cursor;
DEALLOCATE @Cursor;
```

---

## 🎯 결론

### 현재 비동기 방식의 문제
1. ❌ **OFFSET 기반 페이징** + **REPEATABLEREAD** = 배치 간 겹침 가능
2. ❌ **dataTable.Load()의 동기 블로킹** = 비동기의 이점 없음
3. ❌ **배치 간 의존성 불명확** = 경합 조건 발생 가능

### 동기 방식의 장점
1. ✅ **명확한 순차 처리** = 배치 간 완전 분리
2. ✅ **성공 건수 기반 OFFSET** = 중복 없음
3. ✅ **트랜잭션 격리 보장** = 일관된 데이터

### 권장안
**현재 구조를 동기로 변경** (성능은 같지만 신뢰성 ↑↑↑)

```csharp
// 변경 전
await MigrateBatchAsync();  // 비동기, 하지만 블로킹

// 변경 후  
MigrateBatchSync();  // 동기, 명확한 순서
```

### 구현 우선순위
1. **즉시**: `MigrateBatchAsync` → `MigrateBatchSync` 변경
2. **검증**: OFFSET 계산을 **성공 건수** 기반으로 변경
3. **모니터링**: 중복 발생 여부 확인

---

## 📝 테스트 계획

### 동기 방식 테스트
```csharp
[Test]
public void TestSyncMigrationNoDuplicates()
{
    // 1. 100,000개 행 준비
    // 2. BatchSize = 1,000
    // 3. 동기 방식 마이그레이션 실행
    
    // 검증
    var sqlCount = GetCountFromSqlServer();
    var oracleCount = GetCountFromOracle();
    
    Assert.AreEqual(sqlCount, oracleCount);  // ✓ 행 수 동일
    Assert.AreEqual(0, GetDuplicateCount());  // ✓ 중복 0개
}
```

### 성능 비교
```csharp
// 현재 (비동기)
비동기 방식 마이그레이션: 150초

// 변경 후 (동기)  
동기 방식 마이그레이션: ~150초 (성능 거의 같음)
+ 중복 0건 ✓
```
