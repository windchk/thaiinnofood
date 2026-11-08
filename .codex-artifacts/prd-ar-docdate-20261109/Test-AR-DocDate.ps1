$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$main=Get-Content -LiteralPath (Join-Path $root 'OdooSyncWorker\Scripts\Apply_PRD_AR_DocDate_Cutoff_Backfill.sql') -Raw
$declaration=[regex]::Match($main,'(?s)DECLARE @Candidates TABLE\s*\(.*?\n\);').Value
$selection=($main -split '-- BEGIN BACKFILL SELECTION \(shared with the fixture test\)',2)[1]
$selection=($selection -split '-- END BACKFILL SELECTION',2)[0]
if(-not $declaration -or -not $selection){throw 'Selection test seam was not found.'}
$selection=$selection.Replace('TIF_GOLIVE.dbo.OINV','#Tif').Replace('SBO_STL_GOLIVE.dbo.OINV','#Stl').Replace('dbo.INT_OdooQueue','#Queue')
$fixture=@'
SET NOCOUNT ON;
CREATE TABLE #Expected(CaseId INT PRIMARY KEY,ExpectedDecision NVARCHAR(40));
INSERT #Expected VALUES
(101,'EXCLUDED'),(102,'QUEUE_NEW'),(103,'QUEUE_NEW'),(104,'QUEUE_NEW'),
(105,'EXCLUDED'),(106,'EXCLUDED'),(107,'EXCLUDED'),(108,'EXCLUDED'),(109,'EXCLUDED'),(110,'QUEUE_NEW'),
(111,'SKIP_SUCCESS'),(112,'REVIEW_PREVIOUS_ATTEMPT'),(113,'REVIEW_PREVIOUS_ATTEMPT'),(114,'REVIEW_PREVIOUS_ATTEMPT'),
(115,'REVIEW_PREVIOUS_ATTEMPT'),(116,'SKIP_PENDING'),(117,'SKIP_PENDING'),(118,'SKIP_SUCCESS'),
(119,'QUEUE_NEW'),(120,'QUEUE_NEW'),(121,'SKIP_SUCCESS'),(122,'QUEUE_NEW'),(123,'SKIP_SUCCESS'),
(124,'SKIP_PENDING'),(125,'SKIP_SUCCESS'),(126,'REVIEW_PREVIOUS_ATTEMPT'),(127,'REVIEW_PREVIOUS_ATTEMPT'),(128,'REVIEW_PREVIOUS_ATTEMPT');
CREATE TABLE #Tif(DocEntry INT,DocNum INT,DocDate DATETIME NULL,DocDueDate DATETIME NULL,isIns CHAR(1),CANCELED CHAR(1),DocStatus CHAR(1));
INSERT #Tif SELECT CaseId,CaseId,'20261109','20261209','Y','N','O' FROM #Expected;
UPDATE #Tif SET DocDate='20261108' WHERE DocEntry=101;
UPDATE #Tif SET DocDueDate=NULL WHERE DocEntry=102;
UPDATE #Tif SET DocDueDate='20261108' WHERE DocEntry IN(103,104);
UPDATE #Tif SET DocDate='20261110' WHERE DocEntry=104;
UPDATE #Tif SET DocStatus='C' WHERE DocEntry=105;
UPDATE #Tif SET CANCELED='Y' WHERE DocEntry=106;
UPDATE #Tif SET CANCELED='C' WHERE DocEntry=107;
UPDATE #Tif SET isIns='N' WHERE DocEntry=108;
UPDATE #Tif SET DocDate=NULL WHERE DocEntry=109;
SELECT * INTO #Stl FROM #Tif;
CREATE TABLE #Queue(ObjectType NVARCHAR(30),ObjectKey NVARCHAR(50),ActionType NVARCHAR(20),Status CHAR(1),SiteId NVARCHAR(10),SapDatabaseName NVARCHAR(50),CompanyName NVARCHAR(10),ResponseJson NVARCHAR(MAX));
INSERT #Queue
SELECT CASE WHEN E.CaseId=122 THEN 'ProductionOrder' ELSE 'ARReserveInvoice' END,CONVERT(NVARCHAR(50),E.CaseId),
 CASE WHEN E.CaseId=121 THEN 'CANCEL' WHEN E.CaseId=123 THEN 'RELEASED' ELSE 'ADD' END,
 CASE WHEN E.CaseId=115 THEN 'E' WHEN E.CaseId=116 THEN 'N' WHEN E.CaseId=117 THEN 'P' ELSE 'S' END,
 D.SiteId,D.DatabaseName,D.CompanyName,
 CASE WHEN E.CaseId=112 THEN N'{"result":{"status":400,"results":false}}'
 WHEN E.CaseId=113 THEN N'not-json' WHEN E.CaseId IN(114,115,116,117) THEN NULL
 WHEN E.CaseId=126 THEN N'{"error":{"code":100}}' WHEN E.CaseId=127 THEN N'{"result":{"status":200,"results":false}}'
 WHEN E.CaseId=128 THEN N'{"result":{"status":500,"results":true}}'
 ELSE N'{"result":{"status":200,"results":true}}' END
FROM #Expected E CROSS JOIN(VALUES('TIF_GOLIVE','PRD-TIF','TIF'),('SBO_STL_GOLIVE','PRD-STL','STL')) D(DatabaseName,SiteId,CompanyName)
WHERE E.CaseId BETWEEN 111 AND 128 AND E.CaseId<>119;
UPDATE #Queue SET SapDatabaseName=NULL,SiteId='PRD' WHERE ObjectKey='118';
UPDATE #Queue SET SapDatabaseName=CASE WHEN CompanyName='TIF' THEN 'TEST_INTERFACE' ELSE 'TEST_STL_ODOO' END WHERE ObjectKey='120';
UPDATE #Queue SET SapDatabaseName=N'   ',CompanyName=NULL WHERE ObjectKey='125';
INSERT #Queue VALUES('ARReserveInvoice','119','ADD','S','PRD-STL','SBO_STL_GOLIVE','STL',N'{"result":{"status":200,"results":true}}');
INSERT #Queue SELECT ObjectType,ObjectKey,ActionType,'P',SiteId,SapDatabaseName,CompanyName,NULL FROM #Queue WHERE ObjectKey='124';
'@
$assert=@'
IF EXISTS
(
 SELECT 1 FROM #Expected E CROSS JOIN(VALUES(N'TIF_GOLIVE'),(N'SBO_STL_GOLIVE')) D(DatabaseName)
 LEFT JOIN @Candidates C ON C.DocEntry=E.CaseId AND C.SapDatabaseName=D.DatabaseName
 WHERE COALESCE(C.Decision,N'EXCLUDED')<>CASE WHEN E.CaseId=119 AND D.DatabaseName=N'SBO_STL_GOLIVE' THEN N'SKIP_SUCCESS' ELSE E.ExpectedDecision END
)
 THROW 51400,'Backfill selection fixture failed.',1;
IF EXISTS(SELECT SapDatabaseName,DocEntry FROM @Candidates EXCEPT SELECT D.DatabaseName,E.CaseId FROM #Expected E CROSS JOIN(VALUES(N'TIF_GOLIVE'),(N'SBO_STL_GOLIVE')) D(DatabaseName) WHERE E.ExpectedDecision<>N'EXCLUDED')
 THROW 51401,'Unexpected backfill fixture candidate.',1;
SELECT 'Actual backfill selection SQL' AS TestGroup,56 AS Cases,56 AS Passed;
'@
$batches=[System.Collections.Generic.List[string]]::new()
$batches.Add($fixture+"`n"+$declaration+"`n"+$selection+"`n"+$assert)
$baseline=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Baseline.json') -Raw|ConvertFrom-Json
foreach($trigger in @($baseline.Triggers|Where-Object{$_.TriggerName -eq 'TRG_OINV_Reserve_ToOdooQueue' -and $_.DatabaseName -in 'TIF_GOLIVE','SBO_STL_GOLIVE'})){
    $target=$trigger.TriggerDefinition.Replace("I.DocDueDate >= CONVERT(DATE, '20261109', 112)","I.DocDate >= CONVERT(DATE, '20261109', 112)")
    $body=[regex]::Match($target,'(?s)\bAS\s*(BEGIN.*)$').Groups[1].Value
    if(-not $body){throw 'Trigger body test seam was not found.'}
    $body=$body.Replace('INTO TEMP_API.dbo.INT_OdooQueue','INTO @Queued').Replace('FROM TEMP_API.dbo.INT_OdooQueue Q','FROM @History Q').Replace('FROM inserted I','FROM @SourceInserted I').Replace('LEFT JOIN deleted D','LEFT JOIN @SourceDeleted D')
    $company=if($trigger.DatabaseName -eq 'TIF_GOLIVE'){'TIF'}else{'STL'}
    $other=if($company -eq 'TIF'){'STL'}else{'TIF'}
    $setup=@'
SET NOCOUNT ON;
DECLARE @SourceInserted TABLE(DocEntry INT,DocDate DATETIME NULL,DocDueDate DATETIME NULL,isIns CHAR(1),CANCELED CHAR(1));
DECLARE @SourceDeleted TABLE(DocEntry INT,CANCELED CHAR(1));
DECLARE @History TABLE(ObjectType NVARCHAR(30),ObjectKey NVARCHAR(50),ActionType NVARCHAR(20),SiteId NVARCHAR(10),CompanyName NVARCHAR(10),Status CHAR(1));
DECLARE @Queued TABLE(ObjectType NVARCHAR(30),ObjectKey NVARCHAR(50),ActionType NVARCHAR(20),SiteId NVARCHAR(10),CompanyName NVARCHAR(10),Status CHAR(1),RetryCount INT,CreateDate DATETIME);
DECLARE @Expected TABLE(DocEntry INT PRIMARY KEY,ActionType NVARCHAR(20) NULL);
INSERT @Expected VALUES(1,NULL),(2,'ADD'),(3,'ADD'),(4,'ADD'),(5,NULL),(6,NULL),(7,NULL),(8,'CANCEL'),(9,NULL),(10,NULL),(11,NULL),(12,NULL),(13,'ADD'),(14,NULL);
INSERT @SourceInserted SELECT DocEntry,'20261109','20261209','Y','N' FROM @Expected;
UPDATE @SourceInserted SET DocDate='20261108' WHERE DocEntry IN(1,9);
UPDATE @SourceInserted SET DocDueDate='20261108' WHERE DocEntry IN(2,4);
UPDATE @SourceInserted SET DocDate='20261110' WHERE DocEntry=4;
UPDATE @SourceInserted SET DocDate=NULL WHERE DocEntry=5;
UPDATE @SourceInserted SET isIns='N' WHERE DocEntry=6;
UPDATE @SourceInserted SET CANCELED='C' WHERE DocEntry=7;
UPDATE @SourceInserted SET CANCELED='Y' WHERE DocEntry IN(8,9,11);
INSERT @SourceDeleted VALUES(8,'N'),(9,'N'),(10,'N'),(11,'Y'),(14,'N');
INSERT @History VALUES('ARReserveInvoice','12','ADD','PRD','__COMPANY__','N'),('ARReserveInvoice','13','ADD','PRD','__OTHER__','N');
'@
    $setup=$setup.Replace('__COMPANY__',$company).Replace('__OTHER__',$other)
    $verify=@'
IF EXISTS(SELECT 1 FROM @Expected E LEFT JOIN @Queued Q ON TRY_CONVERT(INT,Q.ObjectKey)=E.DocEntry WHERE COALESCE(E.ActionType,'EXCLUDED')<>COALESCE(Q.ActionType,'EXCLUDED'))
 THROW 51402,'Actual A/R trigger body fixture failed.',1;
IF (SELECT COUNT(*) FROM @Queued)<>5 THROW 51403,'Unexpected trigger fixture output count.',1;
SELECT '__DATABASE__ actual trigger body' AS TestGroup,14 AS Cases,14 AS Passed;
'@
    $batches.Add($setup+"`n"+$body+"`n"+$verify.Replace('__DATABASE__',$trigger.DatabaseName))
}
if($batches.Count -ne 3){throw 'Expected backfill and both AR trigger fixture batches.'}
$fixtureFile=Join-Path $PSScriptRoot 'FixtureTests.sql'
($batches.ToArray() -join "`nGO`n")|Set-Content -LiteralPath $fixtureFile -Encoding utf8
& (Join-Path $PSScriptRoot 'Invoke-SqlFile.ps1') -SqlFile $fixtureFile -ResultFile (Join-Path $PSScriptRoot 'FixtureTestResults.json')
