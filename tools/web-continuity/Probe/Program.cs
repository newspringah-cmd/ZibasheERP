using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

// Deliberately uses no AppDbContext, migrations, SaveChanges, writes or outbound messaging.
var raw=Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")??throw new Exception("Connection is required");
var cs=new SqlConnectionStringBuilder(raw);
if(cs.InitialCatalog!="ZibasheERPDb"||cs.UserID.Equals("sa",StringComparison.OrdinalIgnoreCase)||Environment.GetEnvironmentVariable("Telegram__OwnerUserId")!="741471928")throw new Exception("Unexpected target");
cs.ApplicationName="ZibasheContinuityReadOnlyP0";cs.ApplicationIntent=ApplicationIntent.ReadOnly;
await using var connection=new SqlConnection(cs.ConnectionString);await connection.OpenAsync();
using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
async Task<List<object[]>> Query(string sql)
{
 await using var cmd=connection.CreateCommand();cmd.CommandText="SET LOCK_TIMEOUT 2000; SET DEADLOCK_PRIORITY LOW; "+sql;cmd.CommandTimeout=10;
 await using var reader=await cmd.ExecuteReaderAsync(timeout.Token);var rows=new List<object[]>();
 while(await reader.ReadAsync(timeout.Token)){var values=new object[reader.FieldCount];reader.GetValues(values);rows.Add(values);}return rows;
}
var start=DateTime.UtcNow;
var database=await Query("SELECT CONVERT(nvarchar(64),SERVERPROPERTY('Edition')),CAST(SUM(CASE WHEN type=0 THEN size ELSE 0 END)*8.0/1024 AS decimal(18,2)),CAST(SUM(CASE WHEN type=1 THEN size ELSE 0 END)*8.0/1024 AS decimal(18,2)) FROM sys.database_files");
var counts=await Query("SELECT (SELECT COUNT_BIG(*) FROM Customers WHERE IsDeleted=0),(SELECT COUNT_BIG(*) FROM Orders WHERE IsDeleted=0),(SELECT COUNT_BIG(*) FROM Invoices WHERE IsDeleted=0),(SELECT COUNT_BIG(*) FROM SalesLists WHERE IsDeleted=0),(SELECT COUNT_BIG(*) FROM Orders WHERE IsDeleted=0 AND CreatedAt>=DATEADD(day,-30,SYSUTCDATETIME())),(SELECT COUNT(DISTINCT CustomerId) FROM Orders WHERE IsDeleted=0 AND CreatedAt>=DATEADD(day,-30,SYSUTCDATETIME()))");
var invoices=await Query("SELECT Status,COUNT_BIG(*) FROM Invoices WHERE IsDeleted=0 GROUP BY Status ORDER BY Status");
var outbox=await Query("SELECT Channel,Status,COUNT_BIG(*) FROM NotificationOutbox WHERE IsDeleted=0 GROUP BY Channel,Status ORDER BY Channel,Status");
var photos=await Query("SELECT COUNT_BIG(*) FROM SalesLists WHERE IsDeleted=0 AND TelegramPhotoFileId IS NOT NULL AND TelegramPhotoFileId<>''");
var contacts=await Query("SELECT Mobile,TelegramId,Username FROM Customers WHERE IsDeleted=0");
string Normalize(object value)
{
 var s=Convert.ToString(value)?.Trim()??"";s=string.Concat(s.Select(c=>char.GetUnicodeCategory(c)==UnicodeCategory.DecimalDigitNumber?(char)('0'+(int)char.GetNumericValue(c)):c));
 s=s.Replace(" ","").Replace("-","");if(s.StartsWith("0098"))s="0"+s[4..];else if(s.StartsWith("+98"))s="0"+s[3..];else if(s.StartsWith("98")&&s.Length==12)s="0"+s[2..];
 return s.Length==11&&s.StartsWith("09")&&s.All(char.IsAsciiDigit)?s:"";
}
var normalized=contacts.Select(r=>Normalize(r[0])).Where(s=>s!="").ToArray();
Console.WriteLine(JsonSerializer.Serialize(new{SchemaVersion=1,StartedUtc=start,CompletedUtc=DateTime.UtcNow,Scope="read-only aggregate snapshot, not a transactionally frozen financial audit",Database=new{Edition=database[0][0],DataAllocatedMiB=database[0][1],LogAllocatedMiB=database[0][2]},Counts=new{Customers=counts[0][0],Orders=counts[0][1],Invoices=counts[0][2],SalesLists=counts[0][3],OrdersCreatedLast30Days=counts[0][4],CustomersWithOrdersCreatedLast30Days=counts[0][5]},InvoiceStatusCounts=invoices.Select(r=>new{Status=r[0],Count=r[1]}),OutboxCounts=outbox.Select(r=>new{Channel=r[0],Status=r[1],Count=r[2]}),Identity=new{Customers=contacts.Count,SyntacticallyValidIranMobile=normalized.Length,MissingOrNonstandardMobile=contacts.Count-normalized.Length,DuplicateNormalizedMobileGroups=normalized.GroupBy(s=>s).Count(g=>g.Count()>1),MobileOwnershipVerified=false,Note="Syntax is not ownership proof; no contact values exported"},Assets=new{SalesListsWithTelegramPhotoReference=photos[0][0],IndependentArchiveCoverage="not verified"},RestoreEvidence="not verified in this run",WritesPerformed=false,CustomerIdentifiersExported=false}));
