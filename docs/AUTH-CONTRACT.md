# Hợp đồng Authentication & Account — Assignment 2

Trạng thái: chốt thiết kế cho Task A2-01 ngày 2026-10-14. Tài liệu này mô tả **hành vi cần triển khai**, chưa khẳng định API/DB hiện đã có. Nguồn bắt buộc là `PRN232_Assignment2_Requirements.md` ở workspace cha (nằm ngoài repo Backend); quy ước API hiện tại xem [API-CONTRACT.md](API-CONTRACT.md). Các mục ghi **quyết định triển khai** là lựa chọn kỹ thuật để các task tiếp theo làm nhất quán, không phải yêu cầu mới của đề.

## 1. Phạm vi và quyền truy cập

| Nhóm endpoint | Anonymous | Staff (Role 0) | Admin (Role 1) |
| --- | --- | --- | --- |
| Toàn bộ GET Assignment 1: departments, projects, tasks, tags, search | Cho phép | Cho phép | Cho phép |
| POST/PUT/DELETE departments, projects, tasks, tags | 401 | Cho phép | Cho phép |
| POST /api/auth/register, POST /api/auth/login | Cho phép | Cho phép | Cho phép |
| GET/PUT/DELETE /api/accounts và /api/accounts/{id} theo route hỗ trợ | 401 | 403 | Cho phép |

Backend kiểm tra quyền trên từng endpoint; ẩn liên kết ở frontend chỉ là trải nghiệm giao diện. Authentication chạy trước authorization. Token thiếu, sai chữ ký hoặc hết hạn trả 401; token Staff hợp lệ gọi Account API trả 403. GET công khai không yêu cầu Bearer và vẫn hoạt động khi token người dùng đã hết hạn. Health/Swagger giữ cấu hình hiện hành; Swagger có Bearer scheme để thử endpoint bảo vệ.

## 2. Schema và quy tắc dữ liệu

- `SystemAccount` có tối thiểu `AccountID` (PK), `FullName`, `Email` (unique), `PasswordHash`, `Role` (0 Staff, 1 Admin), `CreatedDate`. Mật khẩu được hash BCrypt hoặc PBKDF2 với salt; không lưu, log hoặc trả mật khẩu rõ/hash qua API.
- **Quyết định triển khai:** chuẩn hóa email bằng trim và lowercase trước khi ghi/tra cứu; unique constraint của DB bảo vệ cả trường hợp hai request đăng ký cùng lúc. Không đổi email qua Account PUT vì đề chỉ yêu cầu sửa tên hoặc role.
- Đề yêu cầu không xóa account đã tạo bất kỳ Task nào, nên thêm `Task.CreatedByID` nullable tham chiếu account. Task Assignment 1 giữ `null` vì không có bằng chứng người tạo; Task mới ghi AccountID từ token ở server, không nhận `createdById` do client gửi. Kiểm tra xóa tính cả Task active và soft-deleted; FK `RESTRICT/NO ACTION` là lớp bảo vệ cuối.
- **Quyết định triển khai:** không cho xóa hoặc hạ role của Admin cuối cùng. Admin không được tự xóa account đang đăng nhập trong cùng request. Kiểm tra cùng transaction với thao tác sửa/xóa để tránh hai thao tác đồng thời làm mất Admin cuối cùng. Khi xóa bị chặn, trả 400 theo hợp đồng validation hiện có với `errors.operation`. Không tự tạo endpoint Admin register.
- Script nâng cấp DB phải bảo toàn dữ liệu Assignment 1. Backend đang dùng database-first; Task A2-02 sẽ dùng SQL nâng cấp riêng và mapping thủ công. Không chạy lại script tạo DB cũ có `DROP TABLE`.

## 3. Auth API công khai

JSON theo camelCase như Assignment 1; email/password trong request chỉ truyền qua HTTPS khi triển khai.

### POST /api/auth/register

Request:

```json
{"fullName":"Test Staff","email":"staff@example.com","password":"ExampleOnly123!"}
```

- Bắt buộc: `fullName`, `email`, `password`; trim tên/email, tên không chỉ có khoảng trắng, email đúng định dạng. **Quyết định triển khai:** password tối thiểu 8 ký tự; frontend dùng cùng mức tối thiểu. Confirm Password chỉ tồn tại ở form frontend, không gửi lên API.
- Không nhận/không sử dụng `role` từ client. Tài khoản mới luôn Role 0 Staff.
- Thành công: **201 Created**, `Location: /api/accounts/{id}`, body `AccountResponse` bên dưới. Không trả JWT; người dùng đăng nhập riêng. **Quyết định triển khai:** Location là định danh tài nguyên dù GET account cần Admin, tương tự cách Tag Assignment 1 có Location nhưng không có GET detail public.
- Email trùng: **409 Conflict** (kể cả unique collision ở DB). Dữ liệu sai: **400** với `errors` theo camelCase. Không trả thông tin hash.

### POST /api/auth/login

Request:

```json
{"email":"staff@example.com","password":"ExampleOnly123!"}
```

- Thành công: **200 OK**, response:

```json
{
  "token": "<signed JWT>",
  "tokenType": "Bearer",
  "expiresAt": "2026-10-15T00:00:00Z",
  "account": {
    "accountId": 1,
    "fullName": "Test Staff",
    "email": "staff@example.com",
    "role": 0,
    "createdDate": "2026-10-14T00:00:00Z"
  }
}
```

- Email hoặc password sai: **401 Unauthorized**, cùng thông báo chung để không tiết lộ email có tồn tại hay không. Payload sai/thiếu: **400**.
- Các email/password và timestamp ở ví dụ là dữ liệu minh họa, không phải credential test hoặc giá trị seed.

## 4. JWT và phiên frontend

- Token ký bằng secret từ cấu hình môi trường (Render dùng `JWT_SECRET`; development có thể dùng cấu hình local không commit). Không hardcode secret. **Quyết định triển khai:** hạn token 24 giờ, `exp` theo UTC; không có refresh token trong phạm vi bắt buộc.
- JWT payload tối thiểu có `accountId` số, `email`, `role` là chuỗi `Staff` hoặc `Admin` cho role authorization của ASP.NET Core, cùng `exp`. **Quyết định triển khai:** có thêm `roleId` số 0/1 để frontend dùng ổn định; backend phải ánh xạ role claim đúng với `[Authorize(Roles = "Admin")]`. `sub` có thể bằng AccountID; frontend không dựa vào claim không có trong hợp đồng.
- **Quyết định triển khai:** frontend lưu JWT trong `localStorage` (được đề cho phép), dùng dữ liệu account từ login response cho tên/role; khi reload có thể đọc claim/token để khôi phục trạng thái. Không lưu password. Backend vẫn xác thực chữ ký và role ở mỗi protected request; client đọc claim chỉ để hiển thị/điều hướng.
- Mọi POST/PUT/DELETE bảo vệ và Account API gửi `Authorization: Bearer <token>`. GET public không cần token. Frontend kiểm tra `exp` trước khi vào `/admin/*`; không có/hết hạn thì xóa phiên và chuyển `/login`. Khi protected API trả 401, xóa phiên và chuyển `/login`; 403 hiển thị không đủ quyền nhưng giữ phiên. Logout xóa token và về `/`.
- `/admin/accounts` chỉ hiển thị cho Admin; Staff truy cập URL trực tiếp vẫn phải bị API từ chối 403. Các route `*/manage` cũ không được bỏ ngỏ CRUD sau khi chuyển sang `/admin/*`.

## 5. Account API chỉ Admin

`AccountResponse` dùng cho list/detail/update/register:

```json
{"accountId":1,"fullName":"Test Staff","email":"staff@example.com","role":0,"createdDate":"2026-10-14T00:00:00Z"}
```

| Method/route | Request | Thành công | Lỗi chính |
| --- | --- | --- | --- |
| GET /api/accounts | — | 200, mảng `AccountResponse[]` (rỗng là `[]`) | 401/403 |
| GET /api/accounts/{id} | — | 200, `AccountResponse` | 401/403/404 |
| PUT /api/accounts/{id} | `{"fullName":"New Name"}`, `{"role":1}` hoặc cả hai | 200, `AccountResponse` | 400/401/403/404 |
| DELETE /api/accounts/{id} | — | 204, không body | 400 nếu có Task hoặc vi phạm quy tắc Admin; 401/403/404 |

- PUT là **cập nhật từng phần bằng PUT** theo phạm vi đề: field vắng mặt được giữ nguyên; có ít nhất một trong `fullName`/`role`. `fullName` không được null/rỗng/whitespace; `role` chỉ 0 hoặc 1. **Quyết định triển khai:** không nhận email/password/hash trong PUT.
- Tất cả response account không có `passwordHash`. Nếu ID không tồn tại trả 404. Lỗi 400 dùng ProblemDetails với `errors.fullName`, `errors.role` hoặc `errors.operation` như API hiện có. 401/403 do auth middleware phát; frontend dựa vào status, không phụ thuộc body lỗi cụ thể.

## 6. Ma trận kiểm thử cho các task sau

1. Register hợp lệ 201 Staff; role do client gửi không thể tạo Admin; email trùng 409; payload sai 400; DB chỉ có hash.
2. Login đúng 200 với JWT có accountId/email/role/roleId/exp; login sai 401 giống nhau cho email lạ và sai password; secret/expiry được kiểm tra.
3. GET Assignment 1 không token vẫn 200 theo dữ liệu; mọi write endpoint không token/token sai/hết hạn 401; Staff và Admin hợp lệ ghi được.
4. Account API không token 401, Staff 403, Admin list/detail/update/delete; 404 cho ID lạ; không có PasswordHash trong response.
5. Account có Task, kể cả Task inactive, không xóa được; Task cũ không có tác giả vẫn đọc được; Task mới ghi đúng AccountID từ JWT.
6. Frontend login/logout, guard `/admin/*`, 401 khi hết hạn, 403 giữ phiên, navbar theo role và các trang public còn hoạt động.
