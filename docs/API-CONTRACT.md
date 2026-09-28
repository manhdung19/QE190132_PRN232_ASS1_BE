# Hợp đồng API — task 001

Đối chiếu README_Assignment_1_TaskTrack.md, SQL gốc và model scaffold ngày 2026-09-28. Các lựa chọn bên dưới là **quyết định triển khai**, không phải yêu cầu bổ sung của đề. Task 001 chỉ thêm tài liệu; task 002–004 xây nền tảng, task 005–015 mới triển khai endpoint.

## Quy ước chung

- JSON camelCase, ID dạng `departmentId`, `projectId`, `taskId`, `tagId`, `tagIds`; enum là số. DTO response không chứa entity/navigation EF.
- GET danh sách trả 200 với mảng (rỗng là `[]`), GET chi tiết và PUT trả 200 với detail DTO. POST trả 201 với detail DTO và Location `/api/{collection}/{id}`. Tag không có GET detail bắt buộc: Location là định danh tài nguyên, chưa có endpoint GET tương ứng trong 24 endpoint.
- DELETE thành công trả 204 không body. Route ID không tồn tại hoặc inactive trả 404. Payload sai/FK không tồn tại hoặc inactive, tên tag trùng, xóa bị chặn trả 400. Lỗi hệ thống trả 500 an toàn.
- Lỗi dùng ProblemDetails: `type`, `title`, `status`, `instance`, `traceId`; lỗi 400 thêm `errors` là map camelCase field -> mảng thông báo. Lỗi toàn thao tác dùng key `operation`, JSON sai dùng `body`. Không trả SQL, exception, credential.
- Department/Project/Task chỉ hiện bản ghi có IsActive=true trong list/search/detail và danh sách con. PUT/DELETE inactive trả 404, không có chức năng khôi phục. Không ẩn một bản ghi active chỉ vì cha inactive. Endpoint theo cha yêu cầu cha active, nếu không trả 404; bộ lọc search với ID dương không tìm thấy trả `[]`.
- FK được gán trong create/update phải tồn tại và active. Tag không có IsActive. Kiểm tra liên kết khi xóa phải tính cả bản ghi inactive. Department/Project hard delete khi không còn con; Tag hard delete khi không có TaskTag; Task chỉ soft delete.
- Tìm kiếm substring không phân biệt hoa thường, trim đầu/cuối; null/rỗng/whitespace là bỏ bộ lọc. `%` và `_` là ký tự thường, không phải wildcard của client. Các bộ lọc kết hợp AND; ID lọc phải >0, enum 0–3. Không phân trang, không đăng nhập. Danh sách sắp theo ID tăng dần.
- Chuỗi bắt buộc không chấp nhận whitespace; giữ nguyên giá trị hợp lệ (không tự trim dữ liệu ghi). TagName unique phân biệt hoa thường theo unique constraint SQL hiện có.
- PUT thay toàn bộ trường ghi, default giống create: status=0, priority=1, isActive=true. Không nhận createdDate/modifiedDate để ghi. `tagIds` thiếu/null/[] là tập rỗng; loại ID trùng, ID <=0 lỗi 400. PUT Task thay toàn bộ tags, không nối thêm.
- DATE dùng `yyyy-MM-dd`; startDate bắt buộc, endDate >= startDate nếu có. Không cấm ngày quá khứ, không giới hạn dueDate theo Project.
- TIMESTAMP without time zone: dữ liệu mới là giờ UTC lưu với DateTimeKind.Unspecified, JSON không có offset; client hiểu là UTC. Không suy đoán/chuyển đổi múi giờ seed cũ. Server đặt createdDate, modifiedDate khi cập nhật Task (kể cả soft delete). Task và thay tags được lưu bằng một SaveChangesAsync, transaction ngầm EF bảo đảm nguyên tử.

## 24 endpoint

| # | Method và route | Request | Response |
|---|---|---|---|
| 1 | GET /api/departments | — | DepartmentListItem[] |
| 2 | GET /api/departments/{id} | — | DepartmentDetail + active projects |
| 3 | POST /api/departments | DepartmentCreateRequest | DepartmentDetail |
| 4 | PUT /api/departments/{id} | DepartmentUpdateRequest | DepartmentDetail |
| 5 | DELETE /api/departments/{id} | — | 204 / 400 nếu có Project |
| 6 | GET /api/departments/search | name? | DepartmentListItem[] |
| 7 | GET /api/projects | — | ProjectListItem[] + departmentName |
| 8 | GET /api/projects/{id} | — | ProjectDetail + active tasks |
| 9 | GET /api/projects/department/{departmentId} | — | ProjectListItem[] |
| 10 | POST /api/projects | ProjectCreateRequest | ProjectDetail |
| 11 | PUT /api/projects/{id} | ProjectUpdateRequest | ProjectDetail |
| 12 | DELETE /api/projects/{id} | — | 204 / 400 nếu có Task |
| 13 | GET /api/projects/search | name?, status?, departmentId? | ProjectListItem[] |
| 14 | GET /api/tags | — | TagListItem[] |
| 15 | POST /api/tags | TagCreateRequest | TagDetail |
| 16 | PUT /api/tags/{id} | TagUpdateRequest | TagDetail |
| 17 | DELETE /api/tags/{id} | — | 204 / 400 nếu có TaskTag |
| 18 | GET /api/tasks | — | TaskListItem[] |
| 19 | GET /api/tasks/{id} | — | TaskDetail + tags |
| 20 | GET /api/tasks/project/{projectId} | — | TaskListItem[] |
| 21 | POST /api/tasks | TaskCreateRequest | TaskDetail |
| 22 | PUT /api/tasks/{id} | TaskUpdateRequest | TaskDetail |
| 23 | DELETE /api/tasks/{id} | — | 204, soft delete |
| 24 | GET /api/tasks/search | title?, status?, priority?, projectId?, tagId? | TaskListItem[] |

## Trường DTO

- Department write: departmentName (required, max 100), departmentDescription (required, max 300), isActive. List: thêm departmentId; detail: thêm projects.
- Project write: projectName (required, max 200), description?, startDate (required), endDate?, status, departmentId (>0), isActive. List: thêm projectId, departmentName, createdDate; detail: thêm tasks.
- Tag write: tagName (required, max 50), color? (#RRGGBB, max 7; rỗng được chuẩn hóa null). List/detail: thêm tagId. Không có search endpoint Tag.
- Task write: title (required, max 300), description?, status, priority, dueDate?, projectId (>0), isActive, tagIds?. List: thêm taskId, createdDate, modifiedDate, tags (TagListItem[]); detail cùng trường list.
- Enum Project: 0 NotStarted, 1 InProgress, 2 Completed, 3 OnHold. Task: 0 ToDo, 1 InProgress, 2 Done, 3 Cancelled. Priority: 0 Low, 1 Medium, 2 High, 3 Critical.

## Ví dụ validation

Hợp lệ (FK còn được service kiểm tra):
```json
{"departmentName":"Engineering","departmentDescription":"Software development"}
{"projectName":"Portal","startDate":"2026-09-28","departmentId":1}
{"tagName":"Backend","color":"#123ABC"}
{"title":"Build API","projectId":1,"tagIds":[1,1,2]}
```
Không hợp lệ:
```json
{"departmentName":"   ","departmentDescription":""}
{"projectName":"Portal","departmentId":0,"status":4}
{"tagName":"Backend","color":"red"}
{"title":"Task","projectId":1,"priority":9,"tagIds":[0]}
```
Các field lỗi tương ứng: departmentName/departmentDescription; startDate/departmentId/status; color; priority/tagIds. Model binding sai kiểu hoặc ngày cũng trả 400. Ví dụ lỗi:
```json
{"type":"about:blank","title":"Validation failed","status":400,"instance":"/api/tasks","traceId":"example","errors":{"priority":["The field Priority must be between 0 and 3."]}}
```
