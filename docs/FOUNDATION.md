# Nền tảng task 001–004

Đã hoàn thành ngày 2026-09-28. Hợp đồng triển khai: [API-CONTRACT.md](API-CONTRACT.md).

## Cách mở rộng trong task 005–015

- Controller `[ApiController]` nhận DTO trong Service/Contracts; chỉ gọi service. API chỉ đăng ký DbContext/DI, không truy vấn database trong controller.
- Bốn service scoped hiện có Validate và EnsureExistsAsync cho route active; **chưa có CRUD/search endpoint**. Thêm phương thức nghiệp vụ và inject các repository liên quan + IUnitOfWork khi triển khai từng task. FK payload phải được service kiểm tra và báo `ServiceException.Invalid("projectId", ...)` (400), không gọi EnsureExistsAsync của route (404) thay cho kiểm tra FK.
- Repository cơ sở là thao tác hạ tầng: FindAsync lấy tracked entity kể cả inactive; ReadAsync trả materialized list AsNoTracking; ExistsAsync thực thi predicate trong Repo. Không IQueryable thoát ra ngoài. Không dùng ReadAsync chung để trả thẳng endpoint: các truy vấn active/search/sort/include chuyên biệt sẽ được bổ sung trong Repo ở task tương ứng.
- Kiểm tra HasProjectsAsync/HasTasksAsync không lọc active. TaskRepository không cung cấp hard delete. Add/Remove và chỉnh thuộc tính chỉ stage thay đổi, là thao tác đồng bộ trong bộ nhớ; I/O Find/Read/Exists/LoadTags/Save đều async và nhận CancellationToken.
- Mọi repository và IUnitOfWork dùng cùng DbContext scoped. Với Task update, lấy Task tracked, LoadTagsAsync, lấy Tag tracked qua FindAsync, thay collection Tags rồi gọi **một** SaveChangesAsync sau tất cả kiểm tra. Task create cũng gắn tags trước cùng một lần save. Không save riêng từng tag, không gọi save giữa chừng. EF Core transaction mặc định bao phủ lần save; không thêm migration/schema/global query filter.
- Mapping trong Service/Mapping là thủ công, không I/O. Trước khi mapping, Repo phải Include Department cho Project, Projects.Department cho Department detail, Tasks.Tags cho Project detail, Tags cho Task. Không dùng lazy loading. Mapper lọc active collection con và không đưa navigation vòng lặp vào DTO.
- DatabaseTime.UtcNow cung cấp UTC với Kind=Unspecified phù hợp timestamp without time zone. Service CRUD sẽ đặt timestamp; request không có các trường này.
- RequestValidation dùng DataAnnotations và validation chéo ngày/TagIds; API tự động báo lỗi model binding và validation field camelCase. Unique/FK tồn tại cần kiểm tra qua Repo khi triển khai nghiệp vụ, sau đó UnitOfWork vẫn bắt SQLSTATE 23503/23505 để xử lý race. Lỗi provider được chuyển thành PersistenceConflictException an toàn, API ánh xạ 400; lỗi khác giữ 500. Middleware áp dụng cho controller sau này; không trả/log exception message, SQL hay connection string từ middleware.
- Tag không có endpoint search/detail GET trong đề nên không tạo TagSearchRequest hoặc controller mở rộng ngoài phạm vi.

## Bằng chứng kiểm tra

Từ thư mục Backend:

```powershell
dotnet build QE190132_PRN232_Ass1_BE.sln --no-restore
dotnet run --project tools/TaskTrack.FoundationCheck
```

FoundationCheck là console harness không thêm test framework, host HTTP loopback cổng ngẫu nhiên và test controller chỉ thuộc assembly công cụ. Không có probe endpoint trong API production. Kiểm tra payload hợp lệ/sai, whitespace, giới hạn chuỗi, enum, ngày, tag IDs, default, timestamp, DTO serialization, DI scoped, mapping FK/unique giả lập và HTTP 400/404/500 an toàn. Không kết nối hoặc ghi database.

Kết quả ngày 2026-09-28: solution build thành công với **0 warnings, 0 errors**; `PASS: 51 foundation checks; no database connection or writes.` Kiểm tra bao gồm cùng scoped DbContext và TaskTag được stage cùng Task trước save. SHA-256 SQL vẫn là `26FC6105C8B97787D8C1034B5CF6CAEDD7FE3D0B88FBECC43850F3B6CCB410E9`.

Giới hạn: chưa kiểm thử CRUD, transaction/race thực tế trên PostgreSQL hay deployment; đó là các task tiếp theo. Không đánh dấu hoàn thành toàn bộ giai đoạn B/C chỉ từ kiểm tra nền tảng này.
