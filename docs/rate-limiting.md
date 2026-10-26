# Rate limiting của Core.Gateway

Tài liệu này mô tả toàn bộ cơ chế giới hạn tải của gateway: gồm những tầng nào, mỗi tầng bảo vệ cái gì, đếm bằng thuật toán nào, hành vi khi Redis lỗi, cách cấu hình, quan sát và xử lý sự cố. Nội dung bám theo code trong `src/Core.Gateway.Api/RateLimiting` và `src/Core.Gateway.Api/Redis`.

## 1. Mục tiêu và nguyên tắc

Gateway phải làm hai việc khác nhau, nên có hai nhóm giới hạn khác nhau:

| Nhóm | Câu hỏi nó trả lời | Số được đặt theo | Chạy ở đâu |
|---|---|---|---|
| **Local** (theo pod) | "Pod này, hoặc backend này, có chịu nổi không?" | **Năng lực đo được** (load test) | Trong bộ nhớ từng pod, trước khi xác thực |
| **Shared** (Redis) | "Client này còn được dùng bao nhiêu?" | **Chính sách sản phẩm** (gói dịch vụ) | Redis dùng chung, sau khi xác thực |

Nguyên tắc thiết kế:

1. **Local chạy trước, không gọi mạng.** Một đợt flood bị chặn trong pod mà không tốn lần gọi Redis nào.
2. **Quota shared tính cho cả cụm.** Thêm hay bớt pod không làm đổi quota của client.
3. **Limit local tăng theo số pod.** Đây là chủ đích: thêm pod thì thêm năng lực.
4. **Redis lỗi không làm gateway ngừng phục vụ.** Mặc định chuyển sang quota dự phòng trong từng pod (mục 7).
5. **Danh tính chỉ lấy từ thứ gateway đã xác thực** (claim), không bao giờ lấy từ header do client gửi.

## 2. Thứ tự xử lý một request

Cấu hình ở `Pipeline/GatewayPipelineExtensions.cs`. Phần liên quan đến giới hạn tải:

```
... CORS
 → [1] GatewayConcurrencyLimiterMiddleware     (bulkhead: số request đang chạy)
 → [2] UseRateLimiter                          (Local: instance + service, fixed window)
 → UseAuthentication
 → [3] GatewayAnonymousRateLimitingMiddleware  (Local: trần theo IP cho request chưa xác thực)
 → [4] GatewayRouteRateLimitingMiddleware      (quota theo route policy, Redis)
 → [5] DistributedRateLimitingMiddleware       (quota client / tenant / API key, Redis)
 → UseAuthorization → YARP (reverse proxy)
```

Mỗi tầng có thể trả lời 429 (hoặc 503 ở tầng 4 và 5 khi cấu hình fail closed). Request nào đi qua hết mới tới backend.

Các endpoint gắn `DisableRateLimiting` (health check) bỏ qua mọi tầng.

## 3. Các tầng chi tiết

### 3.1 Bulkhead: giới hạn request đang chạy

- Class: `Local/GatewayConcurrencyLimiterMiddleware`, limiter dựng ở `GatewayLocalRateLimiter.CreateConcurrencyLimiter`.
- Hai giới hạn gắn với nhau, request phải qua cả hai: **theo service** (cluster backend, mặc định 256) và **theo pod** (mặc định 1024). Validator bắt buộc service không vượt instance.
- Không có hàng đợi. Hết chỗ là 429 ngay.
- Khác với rate limit: nó đếm số request **đang mở**, không đếm số request **đến**. Backend chậm thì request tồn đọng, và bulkhead chặn việc đó dồn vào gateway.
- Metric: `gateway.concurrency.active`, `gateway.concurrency.rejected`, `gateway.concurrency.duration`, mỗi cái có tag `cluster`.

### 3.2 Local limiter theo instance và service

- Class: `Local/GatewayLocalRateLimiter.Create`, gắn làm `GlobalLimiter` của ASP.NET Core (`UseRateLimiter`).
- Hai fixed window nối tiếp: **service** (mặc định 600 request/giây cho mỗi cluster) và **instance** (mặc định 1000 request/giây cho cả pod). Partition service lấy từ cluster id của YARP; request không khớp route dùng chung một khóa `unmatched`.
- Chạy trước xác thực nên **không phân biệt được client**. Nó chỉ bảo vệ pod và backend.
- Là fixed window nên có thể cho tối đa khoảng 2 lần limit quanh ranh giới cửa sổ. Chấp nhận được vì cửa sổ chỉ 1 giây và mục đích là chống quá tải, không phải chia quota công bằng.
- Khi bị chặn: 429 kèm `Retry-After`, qua `GatewayRateLimitResponse.WriteRejectionAsync`.
- Metric của ASP.NET Core: `aspnetcore_rate_limiting_requests_total{aspnetcore_rate_limiting_result}`.

### 3.3 Trần theo IP cho request chưa xác thực

- Class: `Local/GatewayAnonymousRateLimitingMiddleware`.
- Mục đích: chặn flood từ một IP **trước khi** nó tốn một lần gọi Redis.
- Sliding window trong bộ nhớ pod, khóa theo IP. Bỏ qua request đã xác thực.
- Chỉ bật khi có cấu hình `RateLimiting:Local:Anonymous`. Giá trị trong `appsettings.json` là 500 request/phút, manifest Kubernetes đặt 300 request/phút cho mỗi IP trên mỗi pod.
- **Phải đặt cao hơn quota shared của khách ẩn danh** (60/phút). Nếu thấp hơn, trần này sẽ chặn client trước khi Redis kịp áp quota thật.
- Metric: `gateway.ratelimit.anonymous.rejected`.

### 3.4 Quota theo route policy

- Class: `Routes/GatewayRouteRateLimitingMiddleware`.
- Một route khai báo `RateLimitPolicy` trong metadata, trỏ tới policy trong `RateLimiting:Policies`. Quota này **cộng thêm** vào quota chung của client.
- Policy có `PermitLimit`, `WindowSeconds` và `Partition`: `Client` (user, không có thì IP), `Ip`, `Tenant`, `ApiKey`. Chọn Tenant hoặc ApiKey mà caller không có thì quay về `Client`.
- Khóa đếm là `route:{tên policy}:{danh tính}`. Các route dùng cùng tên policy **dùng chung bộ đếm**.
- Route không có policy, hoặc tên policy chưa cấu hình, thì bỏ qua tầng này.
- Dùng cho thao tác nhạy cảm: đăng nhập theo IP, export theo tenant, ghi từ đối tác theo API key (xem `deploy/examples/rate-limiting/tenant-api-key-and-routes.json`).
- Metric: `gateway.ratelimit.route.decisions{policy,result,store}`.

### 3.5 Quota shared theo client, tenant, API key

- Class: `Distributed/DistributedRateLimitingMiddleware`. Chạy sau xác thực.
- Thứ tự: **client → tenant → API key**. Quota nào từ chối đầu tiên thì kết thúc request bằng 429, các quota sau không bị trừ.
  - **Client**: user id (claim `sub`, hoặc `NameIdentifier`) nếu đã xác thực; còn lại tính theo IP. Mặc định 300 request/phút cho user, 60 request/phút cho IP.
  - **Tenant**: tùy chọn, từ claim `tenant_id`.
  - **API key**: tùy chọn, từ claim `api_key_id` do chính gateway gán. Header `X-Api-Key` thô không bao giờ là khóa quota.
- Bật bằng `RateLimiting:Distributed:Enabled`. Mặc định trong `appsettings.json` là tắt, manifest production bật.

## 4. Khóa đếm (partition key)

Định nghĩa ở `Shared/GatewayRateLimitPartition`.

| Loại | Dạng khóa | Ghi chú |
|---|---|---|
| user | `user:{id}` | Với đăng nhập bằng API key, signature hoặc IdP bên ngoài: `user:{độ dài scheme}:{scheme}:{id}` |
| tenant | `tenant:{id}` | Tương tự về scheme |
| api-key | `api-key:{id}` | Tương tự về scheme |
| ip | `ip:{địa chỉ}` | IPv4-mapped IPv6 được đưa về IPv4. IPv6 gom theo tiền tố `/64` |
| route | `route:{policy}:{danh tính}` | Danh tính là một trong các khóa trên |

Hai điểm quan trọng:

- **Tách theo scheme.** Cùng một id đến từ hai IdP khác nhau là hai client khác nhau, không dùng chung hay làm cạn quota của nhau.
- **Gom IPv6 theo /64.** Một máy thường có cả dải /64, nên nếu tính từng địa chỉ thì chỉ cần đổi địa chỉ là né được giới hạn.

IP lấy từ kết nối **sau khi** xử lý forwarded headers, nên cấu hình `GatewaySecurity:KnownNetworks` quyết định IP nào được tin.

## 5. Thuật toán

### 5.1 Local: fixed window

.NET `FixedWindowRateLimiter`. Đếm số request trong cửa sổ cố định, hết cửa sổ thì về 0. Có thể cấu hình `QueueLimit` (mặc định 0, tức không xếp hàng).

### 5.2 Shared: sliding window counter

Code: `Distributed/Store/SlidingWindowRateLimiter`.

Thời gian chia thành các cửa sổ cố định dài `window`. Số request ước lượng là:

```
ước lượng = đếm(cửa sổ hiện tại) + đếm(cửa sổ trước) × (phần cửa sổ trước còn nằm trong cửa sổ trượt)
phần còn lại = (window - đã trôi qua trong cửa sổ hiện tại) / window
```

Cho phép khi `ước lượng <= PermitLimit`.

Quy trình mỗi lần gọi:

1. Tăng bộ đếm cửa sổ hiện tại và đọc bộ đếm cửa sổ trước, trong một batch Redis.
2. Nếu vượt: **hoàn lại** (`DECR`) phần vừa cộng, để request bị từ chối không ăn vào quota; rồi trả Rejected kèm thời gian chờ.
3. Nếu hợp lệ: trả Allowed kèm `Limit`, `Remaining` và thời gian đến hết cửa sổ hiện tại.

Ví dụ: limit 10, cửa sổ 1 giây. Cuối giây thứ nhất client dùng hết 10. Ở 0,1 giây của giây thứ hai, cửa sổ trước còn chiếm 0,9 trọng số, nên ước lượng đã là 9 và chỉ còn 1 request hợp lệ. Nhờ vậy không có cú "gấp đôi quota quanh ranh giới" như fixed window.

**`Retry-After` chính xác.** `CalculateRetryAfter` tính ra đúng khoảng phải chờ để thêm một request vừa vặn, thay vì báo cả cửa sổ. Nếu cửa sổ hiện tại đã đầy thì chờ hết cửa sổ này cộng một phần của cửa sổ kế; nếu chỉ cửa sổ trước đang vướng thì chờ ngắn hơn.

Ràng buộc: cửa sổ phải là số nguyên mili giây, `PermitLimit` và `window` phải dương.

### 5.3 Local fallback: sliding window trong bộ nhớ

`QuotaLimit.ToSlidingWindow()`: 10 đoạn mỗi cửa sổ, không hàng đợi. Dùng cho quota dự phòng (mục 7) và trần ẩn danh (3.3).

## 6. Redis

### 6.1 Dữ liệu trong Redis

| Khóa | Kiểu | Nội dung | Hạn |
|---|---|---|---|
| `{KeyPrefix}:ratelimit:sw:{loại}:{sha256}:{windowMs}:{windowIndex}` | string (số) | Bộ đếm một cửa sổ | 2 × cửa sổ |
| `{KeyPrefix}:ratelimit:instances` | sorted set | Các pod đang sống (member = pod, score = thời điểm heartbeat) | 6 × chu kỳ heartbeat |

- Id người dùng, tenant, API key **không bao giờ nằm thô trong khóa**, chỉ có hash SHA-256. Có test kiểm điều này.
- Mọi khóa đều có hạn, nên client ngừng dùng thì không để lại dữ liệu.
- `Redis:KeyPrefix` (mặc định `gateway`, production `core-gateway:production`) tách các môi trường dùng chung một Redis.

### 6.2 Kết nối

`Redis/RedisConnection`. Chọn các tham số để **lỗi nhanh** thay vì treo request:

- `AsyncTimeout` và `SyncTimeout` = `Redis:TimeoutMilliseconds` (mặc định 250 ms).
- `ConnectTimeout` = `Redis:ConnectTimeoutMilliseconds` (mặc định 2000 ms), `ConnectRetry = 0`, `AbortOnConnectFail = false`.
- `BacklogPolicy = FailFast`: mất kết nối thì lệnh lỗi ngay, không xếp hàng chờ.
- Lỗi Redis, socket hoặc timeout được gói thành `RedisUnavailableException` để tầng trên xử lý thống nhất.

### 6.3 Circuit breaker, cooldown và recovery probe

`Redis/RateLimiting/RedisDistributedRateLimitStore`:

1. Redis khỏe: mọi lời gọi đi thẳng.
2. Một lời gọi lỗi: store chuyển sang **không khả dụng** và chỉ log cảnh báo cho lần lỗi đầu tiên của đợt sự cố.
3. Trong thời gian **cooldown** (`Redis:FailureCooldownSeconds`, mặc định 5 giây): mọi lời gọi trả `Unavailable` ngay, **không đụng Redis**, kèm thời gian còn lại của cooldown.
4. Hết cooldown: đúng **một** request làm **recovery probe**, gọi thử Redis; các request khác vẫn nhận `Unavailable`.
5. Probe thành công: store khỏe lại, quota shared tiếp tục hoạt động trong vòng vài giây, không cần khởi động lại.

Có "phiên bản lỗi" để một lời gọi thành công đến muộn không thể xóa mất một lỗi mới hơn.

## 7. Khi Redis không dùng được

Quyết định ở `Distributed/Fallback/LocalFallbackQuota.Resolve`:

| Tình huống | Hành vi |
|---|---|
| Chưa cấu hình Redis (`Redis:ConnectionString` trống) | Đếm **đủ quota theo từng pod** (sliding window trong bộ nhớ). Ở Production, `Distributed:Enabled=true` mà thiếu Redis thì gateway không khởi động, nên tình huống này chỉ còn ở dev hoặc khi tắt quota shared |
| Redis lỗi, `UseLocalFallbackWhenStoreUnavailable = true` (mặc định) | Đếm trong pod với quota **thu nhỏ** theo tỉ lệ ở dưới |
| Redis lỗi, `UseLocalFallbackWhenStoreUnavailable = false` | **503** kèm `Retry-After` bằng thời gian còn lại của cooldown (fail closed) |

### 7.1 Tỉ lệ quota dự phòng

Vì mỗi pod đếm riêng, nếu cho mỗi pod đủ quota thì cả cụm vượt N lần. Giải pháp là mỗi pod chỉ giữ phần của mình:

- `AutoScaleRatio = true` (mặc định): tỉ lệ = **1 / số pod đang sống**. 100 request/phút với 10 pod thì mỗi pod được 10.
- Chưa đếm được số pod, hoặc `AutoScaleRatio = false`: dùng `LocalRatio` cố định. Mặc định `0.5`; manifest production đặt `0.34` (khoảng 1 / `minReplicas`), vì giá trị này chủ yếu dành cho pod khởi động đúng lúc Redis đang chết.
- Hạn mức sau khi thu nhỏ tối thiểu là 1.
- Khóa của limiter có kèm hạn mức đã thu nhỏ. Nhờ vậy khi số pod đổi thì limiter mới được tạo, thay vì giữ mãi hạn mức cũ.

### 7.2 Heartbeat đếm số pod

`Redis/RateLimiting/RedisInstanceHeartbeatService` (BackgroundService), chạy khi rate limiting, quota shared và `AutoScaleRatio` đều bật và Redis đã cấu hình.

Mỗi chu kỳ (`HeartbeatSeconds`), trong **một** round trip Redis:

1. `ZADD` pod này với điểm là thời điểm hiện tại.
2. `ZREMRANGEBYSCORE` xóa các pod im lặng quá 3 chu kỳ.
3. `ZCARD` đếm số pod còn lại, đưa vào `ActiveInstanceCounter`.
4. `EXPIRE` làm mới hạn của khóa.

Khi pod tắt (scale down), nó `ZREM` chính mình để các pod khác thôi đếm nó ngay, không chờ hết hạn.

Khi Redis lỗi: **giữ nguyên số pod đếm được lần cuối** (chia theo số cũ vẫn gần quota thật hơn là quay về tỉ lệ cố định) và tăng metric lỗi heartbeat.

Giới hạn cần biết:

- Điểm dùng đồng hồ của từng pod. Lệch đồng hồ giữa các node vài giây có thể làm đếm sai ít; TTL 3 chu kỳ hấp thụ được.
- Redis chết lâu trong lúc HPA scale thì số pod bị cũ. Tổng quota vẫn gần mức gốc hơn ratio cố định, nhưng không chính xác.
- Pod `HeartbeatSeconds` mặc định trong class là 30 giây, trong `appsettings.json` là 5 giây. Giá trị càng lớn thì pod chết càng bị đếm lâu (3 chu kỳ).

## 8. Phản hồi cho client

Code: `Shared/GatewayRateLimitResponse`, `Shared/GatewayRateLimitHeaders`.

### 8.1 429 Too Many Requests

Body là `application/problem+json` (RFC 7807) có `correlationId`, kèm `Retry-After` (giây, làm tròn lên, tối thiểu 1) khi biết thời gian chờ.

### 8.2 503 Service Unavailable

Chỉ khi Redis lỗi **và** fallback tắt. `Retry-After` là thời gian còn lại của cooldown, để client không dồn vào một store đang chết.

### 8.3 Header `X-RateLimit-*`

| Header | Ý nghĩa |
|---|---|
| `X-RateLimit-Limit` | Hạn mức của quota đang gần cạn nhất |
| `X-RateLimit-Remaining` | Số request còn lại trong quota đó |
| `X-RateLimit-Reset` | Số giây đến hết cửa sổ hiện tại; khi bị chặn thì bằng `Retry-After` |

- Có trên cả response thành công và 429.
- Một request đi qua nhiều quota (client, tenant, API key, route) thì header báo quota **có `Remaining` thấp nhất**.
- Ghi lúc response bắt đầu (`OnStarting`), nên ghi đè header cùng tên do backend trả.
- Không có khi Redis lỗi và pod đang dùng fallback, hoặc khi chỉ áp limit local, vì lúc đó không có số liệu chung đáng tin.

## 9. Cấu hình

Tất cả nằm dưới `RateLimiting` (biến môi trường: dấu `:` thành `__`, ví dụ `RateLimiting__Local__Instance__PermitLimit`). Config sai làm gateway **không khởi động** (validate khi start).

### 9.1 Chung

| Khóa | Mặc định | Ý nghĩa |
|---|---|---|
| `Enabled` | `true` | Công tắc tổng cho mọi tầng |

### 9.2 `Local`

| Khóa | Mặc định (code) | Ý nghĩa |
|---|---|---|
| `Enabled` | `true` | Bật limiter local và bulkhead |
| `Instance` | 1000 / 1 giây | Request mỗi giây cho cả pod |
| `Service` | 600 / 1 giây | Request mỗi giây cho mỗi cluster backend |
| `Anonymous` | không có (code); 500 / 60 giây (`appsettings.json`) | Trần theo IP cho request chưa xác thực, mỗi pod |
| `InstanceConcurrencyLimit` | 1024 | Request đang chạy tối đa mỗi pod (1 đến 100000) |
| `ServiceConcurrencyLimit` | 256 | Request đang chạy tối đa mỗi cluster, không được vượt instance |

Mỗi quota có dạng `{ "PermitLimit": n, "WindowSeconds": s, "QueueLimit": q }`; `PermitLimit` từ 1 đến 1.000.000, `WindowSeconds` từ 1 đến 86.400.

### 9.3 `Distributed`

| Khóa | Mặc định | Ý nghĩa |
|---|---|---|
| `Enabled` | `false` | Bật quota shared trên Redis |
| `UserIdClaim` | `sub` | Claim chứa user id |
| `TenantIdClaim` | `tenant_id` | Claim chứa tenant id |
| `ApiKeyIdClaim` | `api_key_id` | Claim chứa API key id |
| `Authenticated` | 300 / 60 giây | Quota mỗi user đã xác thực |
| `Anonymous` | 60 / 60 giây | Quota mỗi IP chưa xác thực |
| `Tenant` | không có | Quota mỗi tenant (tùy chọn) |
| `ApiKey` | không có | Quota mỗi API key (tùy chọn) |

Quota shared **không được có `QueueLimit` khác 0** (không xếp hàng).

### 9.4 `Distributed:Fallback`

| Khóa | Mặc định | Ý nghĩa |
|---|---|---|
| `UseLocalFallbackWhenStoreUnavailable` | `true` | Redis lỗi thì dùng quota trong pod; `false` thì trả 503 |
| `AutoScaleRatio` | `true` | Dùng 1 / số pod thay vì `LocalRatio` |
| `LocalRatio` | `0.5` (0,01 đến 1,0) | Tỉ lệ cố định khi chưa đếm được pod |
| `HeartbeatSeconds` | 30 trong code, 5 trong `appsettings.json` (1 đến 60) | Chu kỳ heartbeat |

### 9.5 `Policies`

```json
"Policies": {
  "login":  { "PermitLimit": 10,  "WindowSeconds": 60,   "Partition": "Ip" },
  "export": { "PermitLimit": 5,   "WindowSeconds": 3600, "Partition": "Tenant" }
}
```

Tên policy chỉ gồm chữ, số, `.`, `_`, `-` (tối đa 64 ký tự). Route gắn policy bằng metadata `RateLimitPolicy` trong `reverseproxy.json`.

### 9.6 `Redis`

| Khóa | Mặc định | Ý nghĩa |
|---|---|---|
| `ConnectionString` | trống | Trống = coi như chưa cấu hình Redis (quota theo từng pod) |
| `KeyPrefix` | `gateway` | Tiền tố khóa, chỉ gồm chữ, số, `:`, `_`, `-` |
| `TimeoutMilliseconds` | 250 | Timeout mỗi lệnh (50 đến 5000) |
| `ConnectTimeoutMilliseconds` | 2000 | Timeout kết nối (100 đến 30000) |
| `FailureCooldownSeconds` | 5 | Thời gian nghỉ sau khi lỗi (1 đến 300) |

### 9.7 File mẫu

`deploy/examples/rate-limiting/` có ba cấu hình hoàn chỉnh, được test nạp qua validator:

- `standard.json`: cấu hình production chuẩn.
- `tenant-api-key-and-routes.json`: thêm quota tenant, API key và ba route policy.
- `local-only.json`: tắt quota shared, dùng cho dev hoặc cụm nhỏ chưa có Redis.

Giá trị triển khai Kubernetes nằm ở `deploy/kubernetes/settings.env`.

## 10. Quan sát

### 10.1 Metric

Meter `Core.Gateway.RateLimiting`:

| Metric (Prometheus) | Ý nghĩa |
|---|---|
| `gateway_ratelimit_distributed_allowed_total`, `_rejected_total` | Quota shared được cho phép hoặc bị từ chối (tag `partition_type`) |
| `gateway_ratelimit_redis_unavailable_total` | Số lần thao tác lỗi vì Redis không dùng được |
| `gateway_ratelimit_distributed_fail_open_total` | Quyết định do fallback đưa ra khi Redis lỗi (tên cũ, giữ để không hỏng dashboard) |
| `gateway_ratelimit_local_fallback_total` | Quyết định của quota dự phòng trong pod, kèm kết quả |
| `gateway_ratelimit_route_decisions_total` | Quyết định theo route policy, kèm `store` là `redis` hoặc `local` |
| `gateway_ratelimit_anonymous_rejected_total` | Request bị trần IP cục bộ chặn |
| `gateway_ratelimit_redis_duration` | Thời gian mỗi thao tác Redis |
| `gateway_ratelimit_redis_refund_failed_total` | Request bị từ chối nhưng không hoàn được permit vì Redis lỗi |
| `gateway_ratelimit_instances_active` | Số pod đếm được (gauge); dùng để chia quota dự phòng |
| `gateway_ratelimit_instances_heartbeat_failed_total` | Heartbeat không tới được Redis |

Meter `Core.Gateway.Concurrency`: `gateway_concurrency_active`, `gateway_concurrency_rejected_total`, `gateway_concurrency_duration`, mỗi cái có tag `cluster`.

ASP.NET Core: `aspnetcore_rate_limiting_requests_total` cho limiter local.

### 10.2 Alert (`deploy/observability/prometheus-rules.yaml`)

| Alert | Báo khi | Hành động chính |
|---|---|---|
| `GatewaySheddingLoad` | Hơn 1% request bị limiter local từ chối trong 5 phút | Chờ HPA thêm pod. 429 khi CPU thấp nghĩa là limit local thấp hơn năng lực node: đo lại bằng `tests/load/run-capacity.ps1` |
| `GatewayConcurrencyRejections` | Bulkhead của một cluster từ chối liên tục | Thường là backend đó chậm; sửa backend, đừng vội nâng limit |
| `GatewayAtMaxReplicas` | HPA ở `maxReplicas` 15 phút | Kiểm tra traffic bất thường rồi nâng `maxReplicas` nếu traffic là thật |
| `GatewaySharedQuotaFailOpen` | Redis lỗi, quota đang chạy bằng fallback | Khôi phục Redis; quota shared tự trở lại |
| `GatewayFallbackPodCountStale` | Số pod fallback đếm lệch số replica HPA từ 2 trở lên trong 10 phút | Heartbeat lỗi hoặc Redis từng chết lúc HPA scale |

Dashboard Grafana: `deploy/observability/grafana-dashboard.json` (sinh từ `build_dashboard.py`).

### 10.3 Health check

`DistributedRateLimitingHealthCheck` (`/health/ready`) lấy thử một permit từ khóa probe:

- Quota shared tắt hoặc Redis trả lời: **Healthy**.
- Redis lỗi, fallback bật: **Degraded** (gateway vẫn nhận traffic).
- Redis lỗi, fallback tắt: **Unhealthy** (request cần quota đều ra 503).

## 11. Vận hành

### 11.1 Chọn số

- **Local instance và service:** đo bằng `tests/load/run-capacity.ps1`, đặt cao hơn mức bắt đầu bão hòa của pod khoảng 20 đến 30%, để HPA kịp scale trước khi chạm trần.
- **Concurrency:** đặt theo độ trễ bình thường của backend nhân lưu lượng; HPA trong repo scale khi in-flight khoảng 30% instance limit (component `inflight-autoscaling`).
- **Trần ẩn danh local:** cao hơn quota shared ẩn danh vài lần (hiện 300 so với 60). Mục đích chỉ là chắn Redis khỏi flood.
- **Quota shared:** theo gói dịch vụ. Chỉ áp cho identity đã xác thực hoặc IP ở mức vừa phải.
- **`LocalRatio`:** khoảng `1 / minReplicas` của HPA.

### 11.2 Sự cố thường gặp

| Hiện tượng | Nguyên nhân thường gặp | Cách xử lý |
|---|---|---|
| 429 hàng loạt, CPU thấp | Limit local thấp hơn năng lực pod | Đo lại, nâng `RateLimiting__Local__*` |
| 429 hàng loạt, CPU cao | Traffic vượt năng lực | Chờ HPA; nếu ở `maxReplicas` thì nâng và kiểm tra bot, retry storm |
| 429 ở một cluster, cluster khác ổn | Bulkhead đầy do backend chậm | Sửa backend |
| `GatewaySharedQuotaFailOpen` | Redis lỗi | Khôi phục Redis; xem `gateway_ratelimit_redis_unavailable_total` |
| 503 ngay khi Redis lỗi | `UseLocalFallbackWhenStoreUnavailable=false` | Bật lại fallback hoặc chấp nhận fail closed có chủ đích |
| Client bị 429 dù chưa dùng hết quota | Trần ẩn danh local thấp hơn quota shared, hoặc nhiều client sau một NAT | Nâng `Local:Anonymous`; kiểm tra `KnownNetworks` để IP thật được nhận đúng |
| Quota dự phòng sai kích cỡ | Số pod đếm lệch | Xem `gateway_ratelimit_instances_active`, alert `GatewayFallbackPodCountStale` |

### 11.3 Redis

Gateway chịu được Redis chết, nhưng trong lúc đó quota chung thành xấp xỉ. Production nên dùng Redis có HA (Sentinel hoặc dịch vụ managed). Kết nối lấy từ secret `core-gateway-redis` (biến `Redis__ConnectionString`).

## 12. Test

- Test đơn vị và tích hợp: `dotnet test Core.Gateway.sln`. Các test cần Redis thật sẽ bị bỏ qua nếu không có biến `GATEWAY_TEST_REDIS`.
- Chạy toàn bộ với một Redis dùng một lần: `tests/test-redis.ps1` (dựng container `redis:7.4-alpine`, chạy test, rồi xóa).
- Có test với Redis thật cho: quota chia sẻ giữa các instance, TTL và khóa không chứa id thô, header `X-RateLimit-*` qua pipeline, heartbeat đếm và loại pod.
- Drill sự cố (Redis chết, backend chết, timeout, IdP chết): `tests/load/run-drills.ps1`.

## 13. Giới hạn đã biết

- **Limit local theo service tính theo pod gateway, không theo backend.** Backend không tự scale cùng gateway thì HPA lên nhiều pod sẽ đẩy nhiều tải hơn vào backend. Muốn chặn tổng tải vào một backend cần quota theo cluster trên Redis.
- **Quota shared dùng cửa sổ trượt xấp xỉ**, không phải log từng request. Sai số nhỏ, đổi lại mỗi lần chỉ tốn một batch Redis.
- **Fallback không chính xác tuyệt đối.** Chia theo `1 / số pod` giả định traffic của một client phân bố đều giữa các pod.
- **Header `X-RateLimit-*` chỉ phản ánh quota shared.** Limit local và fallback không xuất hiện trong header.
- **Tên metric `fail_open`** là di sản của tên gọi cũ; ý nghĩa hiện tại là "quyết định do fallback đưa ra".

## 14. Bản đồ code

| Việc | File |
|---|---|
| Đăng ký DI | `RateLimiting/GatewayRateLimitingExtensions.cs` |
| Option và validator | `RateLimiting/GatewayRateLimitingOptions.cs`, `GatewayRateLimitingOptionsValidator.cs`, `Local/LocalRateLimitingOptions.cs`, `Distributed/DistributedRateLimitingOptions.cs`, `Distributed/Fallback/DistributedFallbackOptions.cs`, `Routes/RouteRateLimitPolicyOptions.cs`, `Shared/QuotaLimit.cs` |
| Bulkhead | `Local/GatewayConcurrencyLimiterMiddleware.cs` |
| Limiter local | `Local/GatewayLocalRateLimiter.cs` |
| Trần IP ẩn danh | `Local/GatewayAnonymousRateLimitingMiddleware.cs` |
| Route policy | `Routes/GatewayRouteRateLimitingMiddleware.cs` |
| Quota shared | `Distributed/DistributedRateLimitingMiddleware.cs` |
| Thuật toán cửa sổ trượt | `Distributed/Store/SlidingWindowRateLimiter.cs` |
| Lưu trữ Redis và circuit breaker | `Redis/RateLimiting/RedisDistributedRateLimitStore.cs`, `RedisRateLimitCounterStorage.cs`, `Redis/RedisConnection.cs` |
| Fallback và đếm pod | `Distributed/Fallback/LocalFallbackQuota.cs`, `ActiveInstanceCounter.cs`, `Redis/RateLimiting/RedisInstanceHeartbeatService.cs` |
| Khóa đếm | `Shared/GatewayRateLimitPartition.cs` |
| Phản hồi và header | `Shared/GatewayRateLimitResponse.cs`, `Shared/GatewayRateLimitHeaders.cs` |
| Metric | `Shared/GatewayRateLimitingMetrics.cs`, `Local/GatewayConcurrencyMetricNames.cs` |
| Health check | `Distributed/DistributedRateLimitingHealthCheck.cs` |
| Thứ tự middleware | `Pipeline/GatewayPipelineExtensions.cs` |
