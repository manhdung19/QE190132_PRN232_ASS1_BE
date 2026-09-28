# QE190132_PRN232_Ass1_BE

ASP.NET Core Web API .NET 8. Project references: API -> Service -> Repo.

## Chạy local

Mở terminal trong `Backend`:

```powershell
dotnet tool restore
dotnet build QE190132_PRN232_Ass1_BE.sln
dotnet run --project TaskTrack.API --launch-profile http
```

Swagger: http://localhost:5200/swagger. Health: http://localhost:5200/api/health (chỉ kiểm tra tiến trình API).

API đọc `DATABASE_URL` từ môi trường, chuyển URL PostgreSQL sang Npgsql connection string bằng `PostgresConnection.FromUrl`. Có thể thay bằng `ConnectionStrings__TaskTrack` dạng Npgsql nếu không đặt DATABASE_URL. Không ghi secret vào appsettings/source. ASP.NET Core không tự đọc `.env` hoặc file `DATABASE_URL` ở workspace.

`TaskManagementDbContext` được đăng ký scoped qua DI. Chưa triển khai các repository/service CRUD; controller không truy cập database trực tiếp. Chưa chạy migration hay EnsureCreated.

## Giai đoạn B: import và scaffold đã kiểm tra

Ngày 2026-09-28, đã import thành công vào Render database `tasktrack_ass1`.

- Script gốc: `database/TaskManagementDB_Postgres.sql`, giữ nguyên byte.
- SHA-256: `26FC6105C8B97787D8C1034B5CF6CAEDD7FE3D0B88FBECC43850F3B6CCB410E9`.
- Trước import: xác nhận tên database, không có bảng assignment ở các schema người dùng.
- Import: psql 18, `-X`, `ON_ERROR_STOP=1`, `--single-transaction`; preflight, SQL nguyên bản và kiểm tra chạy cùng transaction.
- Sau import: Department 6, Project 7, Task 17, Tag 10, TaskTag 26.
- Đủ 4 khóa ngoại, 5 khóa chính (TaskTag dùng khóa kép), TagName unique.
- EF Core kiểm tra lại số dòng và navigation trong transaction READ ONLY thành công.

SQL có DROP TABLE cho các bảng assignment, không DROP DATABASE/SCHEMA và không có lệnh cấm chạy trong transaction. Không chạy lại file SQL trực tiếp trên database đã có dữ liệu.

### Mã được scaffold

- `TaskTrack.Repo/Models/`: Department, Project, Task, Tag.
- `TaskTrack.Repo/Data/TaskManagementDbContext.cs`: ánh xạ toàn bộ 5 bảng.
- TaskTag là join entity `Dictionary<string, object>` được EF Core sinh cho quan hệ nhiều-nhiều; không thiếu bảng dù không có TaskTag.cs.
- DATE -> DateOnly; TIMESTAMP without time zone -> DateTime; SMALLINT -> short.
- Chỉ thêm alias C# cho `Task` để tránh trùng System.Threading.Tasks.Task; không thay đổi mapping/schema.
- Scaffold dùng named connection `Name=ConnectionStrings:TaskTrack` và `--no-onconfiguring`, không chứa secret trong code.
- Provider Npgsql EF Core 8.0.11; Design/tools 8.0.31, đã build và truy vấn thực tế thành công.

### Lệnh kiểm tra chỉ đọc

Nếu terminal đã có DATABASE_URL:

```powershell
dotnet run --project tools/TaskTrack.DatabaseCheck
```

Hoặc với file secret cục bộ bên ngoài Backend:

```powershell
dotnet run --project tools/TaskTrack.DatabaseCheck -- --secret-file ../DATABASE_URL
```

Công cụ kiểm tra tên database, số seed ban đầu và các navigation; sẽ báo lỗi nếu dữ liệu seed đã thay đổi sau CRUD. Không in thông tin kết nối.

### Script có thể tái sử dụng

```powershell
# Chỉ import trên database rỗng. Tự dừng nếu đã có bảng assignment.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Import-AssignmentDatabase.ps1

# Chỉ scaffold khi chưa có các file đầu ra; không tự ghi đè.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Scaffold-Database.ps1
```

Các script trên dùng DATABASE_URL từ môi trường. `Scaffold-Database.ps1` cũng nhận `-SecretFile ../DATABASE_URL`. `Import-FromLocalSecret.ps1` đọc file secret ở thư mục cha; không thực thi nội dung file như lệnh.

## Schema

Department 1-n Project; Project 1-n Task; Task n-n Tag qua TaskTag. Khóa ngoại không cascade delete. Status/Priority có chú thích 0–3 nhưng không có CHECK: cần validation ở API giai đoạn C. Task soft delete theo yêu cầu assignment.

## Git

Bạn tự tạo repo public cho thư mục Backend. File DATABASE_URL ở workspace đã được root .gitignore loại trừ; không sao chép file secret vào repo. Không commit bin/obj hay credential. Chưa tạo commit trong phiên làm việc này.
