# Core.Gateway

API gateway viết bằng ASP.NET Core 10 và YARP. Solution gồm gateway, ba API mẫu để chạy local, cùng bộ test đơn vị và tích hợp.

## Tính năng

- Xác thực JWT từ nhiều issuer, API key và chữ ký request RSA.
- Policy theo route, làm sạch header chuyển tiếp và kiểm soát CORS.
- Giới hạn tốc độ cục bộ và quota dùng chung qua Redis.
- Timeout, retry có giới hạn, kiểm tra sức khỏe backend và health checks.
- Correlation ID, structured logging và OpenTelemetry.

## Cấu trúc

| Vị trí | Nội dung |
|---|---|
| src/Core.Gateway.Api/ | Gateway và các tầng authentication, security, rate limiting, Redis, resilience, observability |
| services/ | API mẫu cho users, orders và products |
| tests/Core.Gateway.Api.Tests/ | Test đơn vị và tích hợp |
| tests/fixtures/examples/ | Cấu hình mẫu được kiểm tra bởi test |
| tests/postman/ | Collection để thử các endpoint của gateway |

## Chạy local

Mở terminal riêng cho từng API:

~~~powershell
dotnet run --project services/User.Api --launch-profile http
dotnet run --project services/Order.Api --launch-profile http
dotnet run --project services/Product.Api --launch-profile http
dotnet run --project src/Core.Gateway.Api --launch-profile http
~~~

Trong môi trường Development, xác thực được tắt để thử với các API mẫu. Các route mẫu gồm /api/users, /api/orders, /api/products và alias ngắn tương ứng.

## Kiểm tra

~~~powershell
dotnet restore Core.Gateway.sln
dotnet format Core.Gateway.sln --verify-no-changes --no-restore
dotnet build Core.Gateway.sln -c Release --no-restore
dotnet test Core.Gateway.sln -c Release --no-build --no-restore
pwsh -File tests/smoke-development.ps1
~~~

Các test cần Redis thật chỉ chạy khi đặt GATEWAY_TEST_REDIS trỏ đến Redis đang hoạt động. tests/test-redis.ps1 chạy toàn bộ suite với một Redis tạm.

## Cấu hình

Cấu hình mặc định nằm trong src/Core.Gateway.Api/appsettings*.json; biến môi trường có thể ghi đè các giá trị này. Không lưu credential thật trong file cấu hình hoặc test fixture.
