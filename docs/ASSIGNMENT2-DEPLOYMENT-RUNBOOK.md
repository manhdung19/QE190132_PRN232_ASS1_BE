# Assignment 2 — runbook triển khai Render, PostgreSQL và Vercel

Tài liệu này là **quy trình để thực hiện ở A2-17**, chưa xác nhận hệ thống live đã nâng cấp. Làm trên đúng hai repo `Backend/` (Render Docker web service) và `Frontend/` (Vercel Next.js). Ghi lại URL, thời điểm, commit và kết quả từng bước trong nhật ký A2-17; không ghi database URL, mật khẩu, JWT hoặc token vào repo/log bàn giao.

## 1. Chuẩn bị và điểm dừng trước thay đổi

1. Trong Render/Vercel, xác nhận đúng service/project, nhánh production, commit đang chạy, URL public và quyền truy cập dashboard. Ghi lại deployment đang tốt để rollback.
2. Xác nhận DB đích đang là Assignment 1 và có 5 bảng `Department`, `Project`, `Task`, `Tag`, `TaskTag`. Kiểm tra chưa có `SystemAccount` hoặc `Task.CreatedByID`. Xác nhận nguồn DB thực tế mà Render API đang dùng; không suy đoán từ tên project.
3. Chọn thời gian ít ghi dữ liệu và tạm ngưng thao tác ghi trong thời gian backup/nâng schema. Kết nối bằng tài khoản có quyền DDL. Kiểm tra có đủ chỗ lưu bản backup và khả năng restore. Đọc [hướng dẫn nâng DB](ASSIGNMENT2-DATABASE-UPGRADE.md) và [script nâng cấp](../database/Assignment2_Upgrade.sql) trước khi chạy.
4. Trên máy vận hành, cấu hình `PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER`, `PGPASSWORD` trong phiên terminal bảo mật hoặc dùng cơ chế xác thực PostgreSQL tương đương. Render cung cấp external connection URL/PSQL command ở trang database; API trong cùng Render có thể dùng internal URL. Không dán URL/mật khẩu vào lệnh được lưu trong Git hoặc ảnh chụp màn hình.

## 2. Backup, nâng schema, kiểm tra DB

Từ thư mục `Backend`, dùng `pg_dump` và `psql` cùng major version hoặc mới hơn server. Ví dụ PowerShell dưới đây giả định biến `PG*` đã được thiết lập trong phiên hiện tại:

```powershell
$backupDirectory = Read-Host 'Absolute path to a secure backup directory outside the Git repository'
if (-not (Test-Path -LiteralPath $backupDirectory -PathType Container)) { throw 'Backup directory does not exist' }
$backupPath = Join-Path $backupDirectory ('tasktrack-before-a2-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.dump')
pg_dump -Fc --no-owner --no-privileges -f $backupPath
if ($LASTEXITCODE -ne 0) { throw 'Backup failed: stop deployment' }
pg_restore --list $backupPath | Select-Object -First 10
if ($LASTEXITCODE -ne 0) { throw 'Backup unreadable: stop deployment' }

psql -X -v ON_ERROR_STOP=1 -f database/Assignment2_Upgrade.sql
if ($LASTEXITCODE -ne 0) { throw 'Schema upgrade failed: stop deployment' }
psql -X -v ON_ERROR_STOP=1 -f scripts/verify-assignment2-schema.sql
if ($LASTEXITCODE -ne 0) { throw 'Schema verification failed: stop deployment' }
```

Giữ file `.dump` ở nơi bảo mật **ngoài repo**; không commit. So sánh số hàng/ID đại diện của năm bảng Assignment 1 trước và sau upgrade. Script nâng cấp chạy một lần trong transaction; không chạy lại `database/TaskManagementDB_Postgres.sql` vì file đó xóa bảng. Nếu script báo schema đã có hoặc mới nâng một phần, dừng và kiểm tra trạng thái DB. `verify-assignment2-schema.sql` chỉ đọc; `test-assignment2-schema.sql` có ghi tạm và chỉ dành cho DB test.

## 3. Render Backend

Service dùng Dockerfile ở gốc repo Backend; container nghe cổng `8080`, còn `PORT` của Render nếu có sẽ được `Program.cs` áp dụng. Đặt HTTP health check path `/api/health`. Trong Render **Environment**, cấu hình:

| Biến | Giá trị cần đặt | Lưu ý |
| --- | --- | --- |
| `DATABASE_URL` | URL PostgreSQL đúng DB vừa nâng schema, có TLS | Nếu không dùng URL, đặt `ConnectionStrings__TaskTrack` theo Npgsql; không cần cả hai. URL parser chỉ nhận tùy chọn query `sslmode=require`, `verify-ca` hoặc `verify-full`. |
| `JWT_SECRET` | Giá trị ngẫu nhiên tối thiểu 32 byte UTF-8 | Giữ kín và ổn định giữa các lần deploy. Đổi secret sẽ làm JWT cũ hết hiệu lực. |
| `Cors__AllowedOrigins__0` | Origin Vercel production, ví dụ `https://<ten-du-an>.vercel.app` | Chỉ origin, không thêm path, `/api` hay dấu `/` cuối. Các origin local nằm trong `appsettings.json`; có thể cấu hình thêm `__1` cho custom domain. |
| `Swagger__EnableInProduction` | `true` nếu cần Swagger để chấm/kiểm tra; `false` nếu không cần | Cấu hình mặc định trong `appsettings.json` hiện là `true`; đặt rõ trong dashboard theo mục đích sử dụng. |

`Cors:AllowedOrigins` hiện còn cho phép các origin có host kết thúc bằng `vercel.app` trong code, nên giá trị cấu hình trên **không giới hạn mọi Vercel preview domain**. Không xem allowlist này là ranh giới bảo mật; quyền ghi vẫn do JWT xác thực. Nếu cần chỉ cho phép domain cụ thể, chỉnh policy và test lại trước khi triển khai.

Sau khi cấu hình, deploy commit Backend Assignment 2 mới nhất bằng Render Deploys (hoặc để auto deploy đúng nhánh) và xác nhận deploy thành công. Đọc log khởi động; `JWT_SECRET` thiếu sẽ khiến API không lên. Mở `https://<backend-domain>/api/health` (200, `status: ok`), `GET /api/departments` (200) và `/swagger` nếu đã bật. Health endpoint chỉ chứng minh process đáp ứng; GET tài nguyên mới kiểm tra được kết nối DB. GET 500 hoặc lỗi thiếu `CreatedByID` nghĩa là DB/config chưa đúng: dừng, không chuyển frontend.

## 4. Seed Admin, sau đó Vercel Frontend

Sau khi DB đã nâng cấp và API chạy, tạo Admin đầu tiên **một lần** từ terminal tương tác trong thư mục Backend, với `DATABASE_URL` hoặc `ConnectionStrings__TaskTrack` trỏ đúng DB đích. Tool hỏi mật khẩu ẩn và tạo PBKDF2 hash; không có API public để đăng ký Admin:

```powershell
dotnet run --project tools/TaskTrack.AdminSeed -- 'Tên Admin' 'admin@your-domain.example'
```

Xác minh lệnh trả về `Created Admin AccountID ...`; đăng nhập bằng Admin ở UI/API để kiểm tra role. Không seed lặp email, không đưa mật khẩu vào tham số lệnh hoặc file. Nếu DB không cho kết nối từ máy vận hành, cần người quản trị cấp đường kết nối an toàn/terminal phù hợp; không sửa DB bằng câu lệnh chèn password thô.

Trong Vercel project nối với repo Frontend, đặt **Production** `NEXT_PUBLIC_API_URL=https://<backend-domain>/api` (HTTPS, có `/api`, không thêm `/` cuối). Giá trị này được đưa vào bundle client khi build, nên **phải tạo deployment mới** sau khi đổi biến. Xác nhận nhánh/commit Frontend Assignment 2 đang được deploy; kiểm tra Build thành công và domain production. Không đặt secret vào biến `NEXT_PUBLIC_*`.

## 5. Smoke test live và lưu bằng chứng

Thực hiện trên URL live, không dùng mock API. Ghi status/ảnh chụp đã che dữ liệu nhạy cảm và commit/deploy ID trong A2-17:

1. GET backend `/api/health`, `/api/departments`, `/swagger/v1/swagger.json` nếu Swagger bật. GET tài nguyên phải public; Swagger có Bearer scheme cho write và Accounts.
2. Mở frontend ẩn danh: trang public tải danh sách; vào `/admin` chuyển tới `/login`; các CTA quản lý không xuất hiện.
3. Tạo **Staff test** qua `/register` hoặc `POST /api/auth/register` (201); đăng ký email trùng trả 409; login trả JWT. Dùng email test không trùng người thật, bảo quản/xóa tài khoản test theo quy trình của người vận hành.
4. Staff mở dashboard, tạo/sửa/xóa một Tag test độc lập, hoặc đối tượng nghiệp vụ test khác an toàn; kiểm tra thao tác ghi không token trả 401 và Staff gọi `/api/accounts` trả 403. Kiểm tra Task mới lấy `CreatedByID` từ tài khoản hiện tại nếu có phép thử Task phù hợp.
5. Admin login, mở `/admin/accounts`, thấy danh sách; kiểm tra các thao tác account không tiết lộ `PasswordHash`. Thử sửa/xóa chỉ trên account test thích hợp, không xóa Admin cuối hay tài khoản có Task thật.
6. Kiểm tra browser Network: frontend gọi đúng backend HTTPS, preflight có `Authorization` được chấp nhận cho origin đang dùng; logout xóa phiên; token sai/hết hạn đưa người dùng về login. Xác minh domain Render và Vercel đang trỏ deployment mong muốn.

Không chạy 6 công cụ hồi quy ghi/xóa dữ liệu trên production; A2-15 đã chạy chúng trên PostgreSQL tạm. Nếu smoke test cần dữ liệu ghi, tạo bản ghi riêng có tên rõ và dọn sạch sau khi xác minh.

## 6. Khi có lỗi và rollback

- **Upgrade SQL thất bại trước COMMIT:** transaction tự rollback; xác minh lại DB, sửa nguyên nhân rồi lập kế hoạch chạy lại. Không chạy lại script import Assignment 1.
- **Backend mới lỗi sau DB upgrade:** xem log/health/GET và cấu hình. Có thể rollback deploy Render về bản trước trong Deploys; schema A2 thêm bảng/cột nullable nên bản cũ thường vẫn đọc dữ liệu Ass1, nhưng phải xác minh thực tế. Render rollback code không hoàn tác DB.
- **Frontend lỗi:** dùng Vercel Instant Rollback về deployment production trước đó và kiểm tra lại URL. Deployment cũ có thể giữ giá trị env cũ; kiểm tra kết nối API. Rollback frontend không hoàn tác Backend/DB.
- **Cần hoàn nguyên DB sau khi đã có tài khoản/Task mới:** không DROP thủ công `SystemAccount`/`CreatedByID`. Phục hồi từ backup vào DB riêng, so sánh mất dữ liệu phát sinh sau backup, rồi chỉ chuyển kết nối khi người quản trị đã chấp thuận. Restore in-place có thể làm mất dữ liệu; cần cửa sổ bảo trì và kế hoạch riêng.
- Sau rollback, kiểm tra lại health, GET public, login, frontend route và ghi deployment/DB version đang chạy. Ngừng auto deploy theo quy trình dashboard nếu commit lỗi có nguy cơ triển khai lại.

## Tài liệu nền tảng

- Render: [Postgres backups](https://render.com/docs/postgresql-backups), [External connection](https://render.com/docs/postgresql-creating-connecting), [Environment variables](https://render.com/docs/configure-environment-variables), [Health checks](https://render.com/docs/health-checks), [Rollbacks](https://render.com/docs/rollbacks).
- Vercel: [Environment variables](https://vercel.com/docs/environment-variables), [Production rollback](https://vercel.com/docs/deployments/rollback-production-deployment).
