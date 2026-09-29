# QE190132_PRN232_Ass1_BE — TaskTrack Management Backend API

> **Môn học:** PRN232 — Kỹ thuật phát triển ứng dụng phân tán  
> **Sinh viên:** QE190132 — Lớp: PRN232  
> **Công nghệ:** ASP.NET Core Web API (.NET 8), Entity Framework Core (Database First), Npgsql, PostgreSQL.  
> **Kiến trúc:** 3 lớp chuẩn phân tách trách nhiệm (API $\rightarrow$ Service $\rightarrow$ Repo).

---

## 1. Kiến trúc hệ thống và phân tách trách nhiệm

Dự án tuân thủ nghiêm ngặt mô hình 3 lớp phân tầng:

```
[ TaskTrack.API ] (Controllers, Middleware, Filters, Swagger, CORS)
        │
        ▼ (chỉ gọi Interfaces)
[ TaskTrack.Service ] (Business Logic, DTOs, Validation, Result Pattern, DateTime/Tag Rules)
        │
        ▼ (chỉ gọi Interfaces)
[ TaskTrack.Repo ] (IUnitOfWork, Repositories, EF Core DbContext, Entity Models)
        │
        ▼
[ PostgreSQL Database ] (Render PostgreSQL Database-First)
```

- **Ranh giới dữ liệu:**
  - **Controllers** chỉ phụ thuộc và gọi `Service`, tuyệt đối không truy cập trực tiếp `DbContext` hay thực thi truy vấn cơ sở dữ liệu.
  - **Services** chỉ phụ thuộc vào `IUnitOfWork` và các `Repository` interfaces, thực hiện validation nghiệp vụ, kiểm tra ràng buộc logic, xử lý lỗi theo kết quả `Result<T>` và điều phối transaction; không chứa mã truy cập DbContext trực tiếp.
  - **Repositories & DbContext** là nơi duy nhất thực thi truy vấn LINQ/SQL đến PostgreSQL.
- **Tính nguyên vẹn của Schema:**
  - Ánh xạ Database-First hoàn toàn từ `TaskManagementDB_Postgres.sql`.
  - Tuyệt đối không thêm/chạy EF Core Migration, không sử dụng `EnsureCreated()`.
  - Mappings entity và kiểu dữ liệu (DateOnly cho DATE, DateTime cho TIMESTAMP, short cho SMALLINT) được bảo tồn nguyên vẹn.

---

## 2. Hướng dẫn cấu hình và chạy Local

### 2.1 Cấu hình chuỗi kết nối (Database URL)
Backend hỗ trợ định dạng URL kết nối của PostgreSQL (`postgres://...` hoặc `postgresql://...`) thông qua helper chuyển đổi an toàn `PostgresConnection.FromUrl`.

1. **Cách 1: Sử dụng biến môi trường (Khuyên dùng khi chạy local / deploy):**
   ```powershell
   $env:DATABASE_URL = "postgresql://user:password@host:port/dbname?sslmode=require"
   dotnet run --project TaskTrack.API --launch-profile http
   ```
2. **Cách 2: Sử dụng file cấu hình (Không commit secret vào git):**
   Cung cấp cấu hình `ConnectionStrings:TaskTrack` trong `appsettings.Development.json` (được gitignore loại trừ).

### 2.2 Chạy ứng dụng
Mở terminal trong thư mục `Backend`:
```powershell
dotnet restore
dotnet build
dotnet run --project TaskTrack.API --launch-profile http
```
- **Cổng mặc định:** `http://localhost:5200`
- **Swagger UI:** `http://localhost:5200/swagger`
- **Health Check:** `http://localhost:5200/api/health`

---

## 3. Cấu hình Swagger, CORS và Hợp đồng lỗi

### 3.1 Swagger & OpenAPI
- Tự động sinh tài liệu chuẩn OpenAPI v1 cho toàn bộ 24 endpoint công khai.
- Cung cấp mô tả chi tiết về các giá trị Enum, hợp đồng lỗi RFC 7807 ProblemDetails và các quy tắc nghiệp vụ quan trọng.
- Được cấu hình bật trong môi trường Development và có cờ cấu hình `Swagger:EnableInProduction: true` cho mục đích kiểm tra và chấm điểm trên Render.

### 3.2 Cấu hình CORS
- Cung cấp Policy `TaskTrackCorsPolicy` cho phép frontend gọi API thông qua cấu hình `Cors:AllowedOrigins`:
  - Mặc định hỗ trợ Next.js local: `http://localhost:3000` và `http://127.0.0.1:3000`.
  - Khi triển khai lên Vercel, origin của Vercel được cấu hình qua biến môi trường hoặc cấu hình JSON mà không cần sửa code.

### 3.3 Hợp đồng lỗi chuẩn RFC 7807 (ProblemDetails)
- **400 Bad Request:** Chứa cấu trúc ProblemDetails kèm từ điển `errors` với khóa camelCase (ví dụ: `title`, `projectId`, `operation`):
  ```json
  {
    "type": "about:blank",
    "title": "Validation failed",
    "status": 400,
    "instance": "/api/tasks",
    "traceId": "0HN123456789",
    "errors": {
      "title": ["Title is required."]
    }
  }
  ```
- **404 Not Found:** Trả về khi tài nguyên không tồn tại hoặc đã bị ẩn (inactive).
- **500 Internal Server Error:** Thông báo chung an toàn qua `ApiExceptionMiddleware`, không bao giờ để lộ SQL exception hay stack trace.

---

## 4. Danh sách 24 Endpoint công khai

Hệ thống cung cấp đủ 24 endpoint công khai, không yêu cầu đăng nhập/token:

### Nhóm Department (6 endpoints)
1. `GET /api/departments` — Lấy danh sách phòng ban đang hoạt động (`IsActive = true`).
2. `GET /api/departments/{id}` — Lấy chi tiết phòng ban kèm danh sách projects đang hoạt động.
3. `GET /api/departments/search?name=` — Tìm kiếm phòng ban theo tên (không phân biệt hoa thường).
4. `POST /api/departments` — Tạo mới phòng ban (Location header: `/api/departments/{id}`).
5. `PUT /api/departments/{id}` — Cập nhật thông tin phòng ban.
6. `DELETE /api/departments/{id}` — Xóa phòng ban (Chặn xóa trả về 400 nếu có Project liên kết).

### Nhóm Project (7 endpoints)
7. `GET /api/projects` — Lấy danh sách dự án đang hoạt động (`IsActive = true`) kèm tên phòng ban.
8. `GET /api/projects/{id}` — Lấy chi tiết dự án kèm danh sách tasks đang hoạt động.
9. `GET /api/projects/department/{departmentId}` — Lấy danh sách dự án theo phòng ban.
10. `GET /api/projects/search?name=&status=&departmentId=` — Tìm kiếm và lọc dự án kết hợp.
11. `POST /api/projects` — Tạo mới dự án (Kiểm tra DepartmentId hợp lệ và active).
12. `PUT /api/projects/{id}` — Cập nhật thông tin dự án.
13. `DELETE /api/projects/{id}` — Xóa dự án (Chặn xóa trả về 400 nếu có Task liên kết).

### Nhóm Tag (4 endpoints)
14. `GET /api/tags` — Lấy danh sách toàn bộ nhãn.
15. `POST /api/tags` — Tạo mới nhãn (Kiểm tra duy nhất `TagName`).
16. `PUT /api/tags/{id}` — Cập nhật thông tin nhãn.
17. `DELETE /api/tags/{id}` — Xóa nhãn (Chặn xóa trả về 400 nếu có Task liên kết).

### Nhóm Task (7 endpoints)
18. `GET /api/tasks` — Lấy danh sách công việc đang hoạt động (`IsActive = true`).
19. `GET /api/tasks/{id}` — Lấy chi tiết công việc kèm danh sách tags liên kết.
20. `GET /api/tasks/project/{projectId}` — Lấy danh sách công việc theo dự án.
21. `GET /api/tasks/search?title=&status=&priority=&projectId=&tagId=` — Tìm kiếm và lọc kết hợp công việc.
22. `POST /api/tasks` — Tạo mới công việc kèm danh sách `TagIDs` (nguyên tử trong transaction, tự loại bỏ trùng lặp).
23. `PUT /api/tasks/{id}` — Cập nhật công việc, thay toàn bộ tập tags (`tagIds: []` xóa sạch tags), giữ nguyên `CreatedDate`, cập nhật `ModifiedDate`.
24. `DELETE /api/tasks/{id}` — **Xóa mềm công việc** (chỉ đặt `IsActive = false`, không xóa vật lý Task hoặc TaskTag).

---

## 5. Quy tắc nghiệp vụ cốt lõi

1. **Enum chuẩn theo đề bài:**
   - **ProjectStatus:** `0` = NotStarted, `1` = InProgress, `2` = Completed, `3` = OnHold.
   - **TaskStatus:** `0` = ToDo, `1` = InProgress, `2` = Done, `3` = Cancelled.
   - **TaskPriority:** `0` = Low, `1` = Medium, `2` = High, `3` = Critical.
2. **Quy tắc Xóa mềm (Soft Delete):**
   - Chỉ áp dụng đối với **Task** (`IsActive = false`). Task bị xóa mềm sẽ bị ẩn khỏi các API `GET /api/tasks` và `GET /api/tasks/search`.
   - Bản ghi Task và các liên kết trong `TaskTag` vẫn được lưu trữ vật lý trong cơ sở dữ liệu.
3. **Quy tắc Chặn xóa (Cascade Protection) & Ràng buộc toàn vẹn:**
   - Không cho phép xóa Department nếu có bất kỳ Project nào trực thuộc (HTTP 400 với lỗi `operation`).
   - Không cho phép xóa Project nếu có bất kỳ Task nào trực thuộc (HTTP 400 với lỗi `operation`).
   - Không cho phép xóa Tag nếu có bất kỳ Task nào đang sử dụng (HTTP 400 với lỗi `operation`).
   - **Ràng buộc đặc biệt quan trọng:** Project và Tag có liên kết với Task **đã bị xóa mềm (inactive)** vẫn **tuyệt đối bị chặn xóa** (HTTP 400 `operation`).
4. **Quy tắc Xử lý Tags nguyên tử:**
   - Khi tạo hoặc sửa Task, danh sách `TagIDs` được tự động deduplicate.
   - Nếu bất kỳ TagID nào không tồn tại, toàn bộ thao tác bị hủy (rollback transaction), không tạo bản ghi mồ côi.
   - Thao tác `PUT` thay thế toàn bộ danh sách tags: truyền `tagIds: []` sẽ gỡ bỏ tất cả tags của task đó.
5. **Quy ước Thời gian & Dấu vết:**
   - Múi giờ UTC chuẩn, định dạng Date `yyyy-MM-dd`.
   - `startDate <= endDate` nếu có `endDate`.
   - Khi `PUT` Task: `CreatedDate` được bảo toàn nguyên vẹn từ lúc tạo, `ModifiedDate` được cập nhật thời gian UTC hiện tại.

---

## 6. Các công cụ kiểm thử tự động (tools/)

Dự án cung cấp bộ công cụ console tự động kiểm thử toàn diện trên cơ sở dữ liệu thật:

```powershell
# 1. Kiểm thử End-to-End toàn diện 24 endpoints và 53 quy tắc nghiệp vụ
dotnet run --project tools/TaskTrack.FullE2ECheck -- --secret-file ../DATABASE_URL

# 2. Kiểm thử hợp đồng API, Swagger, CORS và RFC 7807 ProblemDetails
dotnet run --project tools/TaskTrack.ApiContractCheck -- --secret-file ../DATABASE_URL

# 3. Kiểm thử riêng lẻ từng domain
dotnet run --project tools/TaskTrack.TaskCheck -- --secret-file ../DATABASE_URL
dotnet run --project tools/TaskTrack.TagCheck -- --secret-file ../DATABASE_URL
dotnet run --project tools/TaskTrack.ProjectCheck -- --secret-file ../DATABASE_URL
dotnet run --project tools/TaskTrack.DepartmentCheck -- --secret-file ../DATABASE_URL

# 4. Kiểm thử nền tảng (in-memory DTO/Validation)
dotnet run --project tools/TaskTrack.FoundationCheck
```

---

## 7. Đánh giá và Hạn chế hiện tại

- **Cơ sở dữ liệu:** Toàn bộ kiểm thử đã được xác thực trực tiếp và thành công 100% trên cơ sở dữ liệu PostgreSQL thật trên Render. Nếu cần chạy offline hoàn toàn trên instance PostgreSQL local độc lập, máy trạm cần cài đặt PostgreSQL server local và import file script SQL.
- **Frontend & Deployment:** Backend đã sẵn sàng 100% để tích hợp với Frontend Next.js (Giai đoạn D) và triển khai Render Web Service (Giai đoạn E).
