-- SQL Server 테스트 데이터 생성 스크립트
-- 로컬 SQL Server 또는 SQL Server Express에서 실행

-- 1. 테스트 데이터베이스 생성 (기존 데이터베이스가 있으면 삭제)
IF EXISTS (SELECT * FROM sys.databases WHERE name = 'MigrationTestDB')
BEGIN
    DROP DATABASE MigrationTestDB
END

CREATE DATABASE MigrationTestDB
GO

USE MigrationTestDB
GO

-- 2. 샘플 테이블 생성
CREATE TABLE dbo.Employees (
    EmployeeID INT PRIMARY KEY IDENTITY(1,1),
    FirstName VARCHAR(50) NOT NULL,
    LastName VARCHAR(50) NOT NULL,
    Email VARCHAR(100),
    HireDate DATETIME,
    Salary DECIMAL(10,2),
    IsActive BIT
)

CREATE TABLE dbo.Departments (
    DepartmentID INT PRIMARY KEY IDENTITY(1,1),
    DepartmentName VARCHAR(100) NOT NULL,
    Description VARCHAR(500),
    Budget DECIMAL(15,2)
)

CREATE TABLE dbo.Projects (
    ProjectID INT PRIMARY KEY IDENTITY(1,1),
    ProjectName VARCHAR(200) NOT NULL,
    StartDate DATE,
    EndDate DATE,
    Budget DECIMAL(12,2),
    Status VARCHAR(20)
)

-- 3. 샘플 데이터 삽입
INSERT INTO dbo.Departments (DepartmentName, Description, Budget) VALUES
('IT', '정보기술부', 500000),
('HR', '인사부', 300000),
('Finance', '재무부', 400000),
('Sales', '영업부', 600000),
('Marketing', '마케팅부', 350000)

INSERT INTO dbo.Employees (FirstName, LastName, Email, HireDate, Salary, IsActive) VALUES
('김', '철수', 'kim.cs@company.com', '2020-01-15', 45000, 1),
('이', '영희', 'lee.yh@company.com', '2020-02-20', 48000, 1),
('박', '민지', 'park.mj@company.com', '2020-03-10', 42000, 1),
('최', '준호', 'choi.jh@company.com', '2020-04-05', 50000, 1),
('정', '수진', 'jung.sj@company.com', '2020-05-12', 46000, 0),
('한', '동욱', 'han.dw@company.com', '2020-06-18', 44000, 1),
('신', '지영', 'shin.jy@company.com', '2020-07-22', 47000, 1),
('유', '지원', 'yu.jw@company.com', '2020-08-30', 43000, 1),
('윤', '해준', 'yun.hj@company.com', '2020-09-14', 49000, 1),
('홍', '길동', 'hong.gd@company.com', '2020-10-25', 51000, 1)

INSERT INTO dbo.Projects (ProjectName, StartDate, EndDate, Budget, Status) VALUES
('ERP System Migration', '2023-01-01', '2023-06-30', 250000, 'Completed'),
('Data Analytics Platform', '2023-02-15', '2024-02-15', 180000, 'In Progress'),
('Customer Portal Redesign', '2023-03-01', '2023-09-30', 150000, 'Completed'),
('Cloud Migration', '2023-04-01', '2024-03-31', 320000, 'In Progress'),
('Mobile App Development', '2023-05-15', '2024-01-31', 200000, 'Completed')

-- 4. 테이블 확인
PRINT 'Departments:'
SELECT * FROM dbo.Departments

PRINT 'Employees:'
SELECT * FROM dbo.Employees

PRINT 'Projects:'
SELECT * FROM dbo.Projects

PRINT '=========================================='
PRINT '테스트 데이터 생성 완료!'
PRINT '=========================================='
