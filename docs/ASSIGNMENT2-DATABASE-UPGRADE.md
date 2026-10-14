# Assignment 2 — nâng cấp PostgreSQL an toàn

Áp dụng sau khi đã có schema Assignment 1. Script [Assignment2_Upgrade.sql](../database/Assignment2_Upgrade.sql) chỉ tạo `SystemAccount`, thêm `Task.CreatedByID` nullable, FK hạn chế xóa và index. Nó không chạy seed, không sửa/xóa dữ liệu cũ. **Không chạy lại** `TaskManagementDB_Postgres.sql` trên DB đang dùng: file đó bắt đầu bằng `DROP TABLE`.

## Trước khi chạy

1. Xác nhận đúng database, quyền DDL và backup/snapshot có thể khôi phục. Dùng DB test trước khi chạy production.
2. Ghi lại số hàng và một vài ID đại diện của `Department`, `Project`, `Task`, `Tag`, `TaskTag`. Không cần chép dữ liệu nhạy cảm vào log.
3. Kiểm tra DB đã có năm bảng Assignment 1, chưa có `SystemAccount` và `Task.CreatedByID`. Script tự kiểm tra lại và dừng nếu điều kiện không đúng.
4. Kết nối bằng URL/cấu hình riêng trên máy vận hành. Không đưa URL/password vào command được lưu trong repo hoặc tài liệu bàn giao.

Từ thư mục `Backend`, với biến kết nối đã được cấp trong phiên terminal:

```powershell
& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -v ON_ERROR_STOP=1 -f database/Assignment2_Upgrade.sql
& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -v ON_ERROR_STOP=1 -f scripts/verify-assignment2-schema.sql
```

Cấu hình kết nối `psql` bằng biến môi trường PGHOST/PGPORT/PGDATABASE/PGUSER/PGPASSWORD hoặc cơ chế xác thực PostgreSQL của môi trường; không ghi giá trị vào lệnh hoặc repo. Có thể dùng đường dẫn `psql` khác nếu máy không dùng bản 18. Không truyền `--single-transaction` vì file nâng cấp đã có `BEGIN/COMMIT`.

Script được thiết kế **chạy một lần**. Nếu chạy lại, nó dừng trước DDL và không làm gì. Nếu có lỗi giữa chừng, transaction rollback toàn bộ. Nếu thấy thông báo schema đã tồn tại/không đầy đủ, kiểm tra trực tiếp thay vì cố chạy lại.

## Kiểm tra sau khi chạy

- `verify-assignment2-schema.sql` kiểm tra sáu cột account, `Task.CreatedByID` nullable, unique email, check role, FK `ON DELETE RESTRICT` và index. Script chỉ đọc.
- So sánh số hàng/ID của năm bảng cũ với số đã ghi trước khi chạy. Chúng phải giữ nguyên. Toàn bộ Task cũ có `CreatedByID IS NULL`; Task mới ở các task sau phải lấy ID từ JWT phía backend.
- Email được ghi lowercase và trim; DB từ chối giá trị không chuẩn hóa, đồng thời có unique constraint. Role chỉ nhận 0 (Staff) hoặc 1 (Admin). `PasswordHash` chỉ chứa hash do backend/công cụ seed tạo.
- Trên database test dùng dữ liệu seed Assignment 1, có thể chạy [test-assignment2-schema.sql](../scripts/test-assignment2-schema.sql) để thử email trùng, email chưa chuẩn hóa, role sai và FK khi Task đã soft-delete. Bài test tự `ROLLBACK` nhưng vẫn cập nhật tạm một Task trong transaction, **không chạy trên production**.

## Rollback

Nếu transaction nâng cấp thất bại, PostgreSQL tự rollback; **không chạy script phục hồi**. Nếu đã commit nhưng chưa tạo account hoặc gán người tạo cho Task, có thể phục hồi từ snapshot, hoặc sau khi xác nhận dữ liệu mới thật sự rỗng, mở transaction riêng để xóa index/FK/cột `CreatedByID` rồi `SystemAccount`. Việc đó sẽ xóa dữ liệu Assignment 2; không thực hiện khi đã có account/task ownership. Khi production đã có dữ liệu mới, dùng backup và kế hoạch khôi phục được người quản trị xác nhận thay vì chạy lệnh DROP thủ công.

Mapping EF Core database-first và nghiệp vụ account được thêm ở Task A2-03 trở đi; A2-02 chỉ nâng schema.
