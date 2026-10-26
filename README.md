# Core.Gateway

API gateway ASP.NET Core 10 dùng YARP làm proxy engine, cấu trúc theo tính năng: mỗi thư mục là một tầng (authentication, headers, rate limiting, QoS…), mỗi route khai báo policy của nó trong file route. Gateway không giữ state nên scale ngang bằng cách thêm pod; mọi quota cần chia sẻ nằm trên Redis.

## Cấu trúc

| Thư mục (`src/Core.Gateway.Api/`) | Trách nhiệm |
|---|---|
| `Program.cs` | `builder.AddCoreGateway()` → `app.UseCoreGateway()` |
| `DependencyInjection/` | Đăng ký mọi tầng |
| `Pipeline/` | Thứ tự pipeline, một file duy nhất |
| `Configuration/` | Nạp file route, metadata riêng của route |
| `ReverseProxy/` | YARP, config filters, HTTP client tới backend |
| `Authentication/` | Công tắc chung, policy scheme chọn loại credential cho từng request, và quy tắc "route cần người gọi đã xác thực". Gateway chỉ xác thực, phân quyền do service phía sau lo |
| `Authentication/Jwt/` | Hệ thống mình gọi: JWT một hoặc nhiều issuer, khóa từ `Authority` / `SigningKey` / `PublicKeyFile`, fail-closed khi IdP lỗi |
| `Authentication/ApiKey/` | Backend bên thứ ba gọi bằng `X-Api-Key`: options, validator, handler riêng |
| `Authentication/Signature/` | Bên thứ ba ký từng request (kể cả body) bằng RSA, gateway chỉ giữ public key và chặn replay |
| `Authentication/Keys/` | Đọc public key (JWKS, PEM, certificate) dùng chung cho JWT và chữ ký |
| `Headers/` | Claim → header downstream, xóa header giả mạo |
| `RateLimiting/` | `Local/` cầu dao per-pod và cap IP cho người chưa xác thực, `Distributed/` quota chung (`Store/` hợp đồng và sliding window, implementation Redis nằm ở `Redis/RateLimiting/`, `Fallback/` chế độ khi Redis lỗi), `Routes/` quota theo route, `Shared/` partition key, response 429 và metric. Mỗi thư mục có options riêng, options gốc chỉ gom lại |
| `Redis/` | Kết nối Redis dùng chung, nơi lưu bộ đếm quota (`RateLimiting/`) và heartbeat để đếm số pod đang chạy |
| `Resilience/` | Timeout, passive health của backend |
| `Errors/` | ProblemDetails, map lỗi forward sang 502/504 |
| `Security/` | Host filtering, forwarded headers, CORS, security headers |
| `Correlation/`, `Observability/`, `HealthChecks/` | Correlation ID, log/OTel, probes |

Mỗi tầng tự sở hữu options, validator (chạy lúc startup, sai cấu hình thì không khởi động) và extension `AddGateway*`.

Ngoài `src/`:

| Vị trí | Nội dung |
|---|---|
| `tests/Core.Gateway.Api.Tests/` | Test đơn vị và tích hợp, cấu trúc thư mục giống `src/`. Test cần Redis thật chạy bằng `tests/test-redis.ps1` |
| `tests/postman/` | Collection và environment Postman để thử gateway đang chạy (route, header, xác thực, rate limit). Chạy bằng Postman hoặc `npx newman run tests/postman/core-gateway.postman_collection.json -e tests/postman/core-gateway.postman_environment.json` |
| `tests/load/` | Load test (k6), đo năng lực pod, drill sự cố (`run-capacity.ps1`, `run-drills.ps1`) |
| `tests/observability/` | Test cho alert Prometheus và danh sách metric |
| `deploy/kubernetes/` | Manifest (Deployment, HPA, PDB, Ingress, NetworkPolicy), `settings.env` và `reverseproxy.json` |
| `deploy/observability/` | Alert Prometheus và dashboard Grafana |
| `deploy/examples/` | Cấu hình mẫu cho `authentication/` và `rate-limiting/`, được test nạp qua validator |
| `docs/rate-limiting.md` | Tài liệu chi tiết về rate limiting: các tầng, thuật toán, Redis, fallback, cấu hình, xử lý sự cố |

## Cấu hình route

Route và cluster nằm trong `Configuration/reverseproxy.json` (Kubernetes: `deploy/kubernetes/reverseproxy.json`, giữ hai bản giống nhau). Policy được định nghĩa một lần trong `appsettings`/biến môi trường, route chỉ tham chiếu tên:

~~~json
"orders-write-route": {
  "ClusterId": "order-cluster",
  "TimeoutPolicy": "api",
  "Match": { "Path": "/api/orders/{**catch-all}", "Methods": [ "POST", "PUT", "DELETE" ] },
  "Metadata": { "RateLimitPolicy": "strict", "RetryAttempts": "0", "MaxRequestBodySize": "52428800" }
}
~~~

~~~json
"RateLimiting": {
  "Policies": {
    "strict": { "PermitLimit": 10, "WindowSeconds": 60, "Partition": "Client" }
  }
},
"Headers": {
  "ClaimsToHeaders": { "X-User-Id": "sub", "X-Tenant-Id": "tenant_id", "X-Auth-Issuer": "iss" }
},
"Resilience": {
  "TimeoutPolicies": { "reports": 120 },
  "RetryAttempts": 1
}
~~~

- **Phân quyền**: gateway chỉ xác thực. Route không khai gì thì yêu cầu người gọi đã xác thực (thiếu credential trả 401); `"AuthorizationPolicy": "anonymous"` mở route công khai. Người gọi được làm gì do service phía sau quyết định, dựa vào header gateway chuyển xuống (`X-User-Id`, `X-Tenant-Id`…) hoặc `Authorization` với JWT. Muốn service biết scope của client API key/chữ ký thì ánh xạ claim đó qua `Headers:ClaimsToHeaders`, ví dụ `"X-Scopes": "scope"`.
- **RateLimitPolicy**: quota cộng thêm vào quota chung của client. `Partition`: `Client` (user, không có thì IP), `Ip`, `Tenant`, `ApiKey`. Route alias dùng cùng policy thì dùng chung bộ đếm. Có Redis thì đếm chung toàn cluster; chưa cấu hình Redis thì đếm theo từng pod; Redis đang lỗi thì theo `RateLimiting:Distributed:Fallback` giống quota của client: `UseLocalFallbackWhenStoreUnavailable=true` đếm local ở mức `1 / số pod` của hạn mức (`LocalRatio` khi chưa đếm được số pod), `false` trả 503.
- **ClaimsToHeaders**: header do client gửi luôn bị xóa, sau đó gateway gán lại từ token đã xác thực. `X-Auth-Issuer` cho backend biết `X-User-Id` thuộc issuer nào, vì user ID chỉ duy nhất trong phạm vi một IdP. Downstream vẫn nhận `Authorization` để tự kiểm tra.
- **Timeout**: route chọn `"TimeoutPolicy": "reports"` (policy có tên trong `Resilience:TimeoutPolicies`) hoặc đặt thẳng `"Timeout": "00:02:00"`; mặc định là policy `api` (`Resilience:RequestTimeoutSeconds`). Request chạy lâu còn cần tăng `HttpRequest.ActivityTimeout` của cluster (mặc định 20s) và `proxy-read-timeout` của Ingress (40s).
- **RetryAttempts** (0-3, mặc định `Resilience:RetryAttempts` = 1): chỉ retry GET/HEAD/OPTIONS không có body, khi backend không kết nối được và chưa gửi gì về client; lần thử sau chọn destination khác. Timeout và response lỗi (5xx) từ backend không bao giờ được retry.
- **Streaming** (`"true"`): route SSE, long-poll hoặc truyền dữ liệu lâu. Gateway bỏ timeout theo request (`TimeoutPolicy` được đặt thành `Disable`), nên route không được khai thêm `TimeoutPolicy` khác. Stream vẫn bị ngắt khi im lặng quá `HttpRequest.ActivityTimeout` của cluster (mặc định 20s) và chiếm một suất của bulkhead `ServiceConcurrencyLimit` suốt thời gian mở.
- **MaxRequestBodySize** (byte): thay giới hạn chung `GatewaySecurity:MaxRequestBodySizeBytes` (10MB) cho route đó; vượt giới hạn trả 413. Route upload lớn hơn 10MB còn cần tăng `proxy-body-size` của Ingress.
- Route tham chiếu policy không tồn tại thì gateway không khởi động; nếu xảy ra lúc reload thì gateway giữ cấu hình cũ.

**API key** cho client máy-với-máy: client gửi header `X-Api-Key` và nhận claim giống JWT (`sub`/`api_key_id` = tên client, `scope`, `role`, `tenant_id`), nên dùng chung quota và header với JWT. `Scopes`/`Roles` của client không được gateway kiểm tra, chúng chỉ là claim để chuyển xuống service (xem ClaimsToHeaders); `TenantId` còn dùng cho quota theo tenant. Gateway chỉ lưu SHA-256 của key và không bao giờ chuyển key xuống backend.

~~~json
"Authentication": {
  "ApiKeys": {
    "Enabled": true,
    "Clients": {
      "billing-service": { "KeyHash": "<sha256 hex>", "Scopes": [ "orders.write" ], "TenantId": "t1" }
    }
  }
}
~~~

~~~bash
key=$(openssl rand -base64 32)                      # gửi cho client, không lưu ở gateway
printf %s "$key" | sha256sum | cut -d' ' -f1       # KeyHash
~~~

Trên Kubernetes đặt các giá trị này vào secret `core-gateway-api-keys` (`Authentication__ApiKeys__Clients__billing-service__KeyHash=...`). Muốn thu hồi key thì đặt `Enabled: false` cho client đó; muốn đổi key thì tạo client mới, chuyển client sang key mới rồi tắt client cũ.

Nhiều nguồn JWT: khai báo `Authentication:Jwt:Providers:<tên>` với `Issuer`, `Audience`, đúng một trong ba `Authority` / `SigningKey` / `PublicKeyFile`, và `ValidAlgorithms`. `PublicKeyFile` là đường dẫn tới file khóa công khai (ví dụ secret mount vào `/app/keys/kaiva.jwks`). Định dạng tự nhận theo nội dung: **JWKS** (RFC 7517, khuyến nghị: chứa được nhiều khóa chọn theo `kid` nên xoay khóa không gián đoạn), hoặc PEM `PUBLIC KEY`, `RSA PUBLIC KEY`, `CERTIFICATE`. Hỗ trợ RSA (từ 2048 bit, `RS*`/`PS*`) và EC P-256/384/521 (`ES*`); từ chối private key và khóa đối xứng. `ValidAlgorithms` phải khớp loại khóa trong file. Gateway đọc file một lần lúc khởi động, thiếu file hoặc sai định dạng thì không start và thông báo lỗi nêu rõ đường dẫn; đổi khóa thì rollout lại pod. Trên Kubernetes, các giá trị này nằm trong secret `core-gateway-jwt-providers`. Khi `Providers` rỗng, gateway dùng `Authentication:Jwt:Authority` và `Authentication:Jwt:Audience`. **SSO** là một provider JWT có `Authority` (gateway tự lấy khóa qua OIDC discovery và JWKS, theo kịp khi IdP xoay khóa). Khai báo như sau, hoặc đặt các env var tương ứng vào secret `core-gateway-jwt-providers` (mẫu đầy đủ trong `deploy/examples/secrets.example.yaml`):

~~~json
"Authentication": {
  "Jwt": {
    "Providers": {
      "SSO": {
        "Issuer": "https://sso.company.com/realms/main",
        "Authority": "https://sso.company.com/realms/main",
        "Audience": "gateway-api",
        "ValidAlgorithms": [ "RS256" ],
        "RoleClaimType": "roles"
      }
    }
  }
}
~~~

`RoleClaimType` (mặc định `role`) và `NameClaimType` (mặc định `sub`) khai theo từng provider vì mỗi IdP đặt tên claim khác nhau; `Headers:ClaimsToHeaders` đọc claim theo tên gốc trong token. SSO, JWT của đối tác (`PublicKeyFile`) và API key chạy song song: `iss` của token chọn provider, header `X-Api-Key` chọn API key. Khi `Providers` có ít nhất một provider thì `Authentication:Jwt:Authority`/`Audience` ở mức gốc bị bỏ qua; chỉ có một IdP thì dùng hai key gốc đó và để `Providers` rỗng.

**Chữ ký RSA cho bên thứ ba.** Đối tác không cần token hay IdP: họ giữ private key, gateway chỉ giữ public key (`Authentication:Signature:Clients:<tên>:PublicKeyFile`, PEM hoặc JWKS, chỉ RSA). Mỗi request gửi ba header: `X-Client-Id` (tên client), `X-Timestamp` (Unix giây) và `X-Signature` (Base64 của chữ ký). Chuỗi được ký gồm năm dòng:

~~~text
POST
/api/orders?page=2
1767225600
partner-a
5feceb66ffc86f38d952786c6d696c79c2dbc239dd4e91b46729d73a27fb57e9
~~~

tức là method viết hoa, path kèm query đúng như gửi đi, timestamp, client id, và SHA-256 (hex chữ thường) của body (không có body thì là hash của chuỗi rỗng, `e3b0c442…b855`), nối bằng `\n`, không có dòng trống ở cuối. Mặc định thuật toán là `RS256` (RSASSA-PKCS1-v1_5 + SHA-256, đúng thứ `openssl` tạo ra); đặt `Algorithm: PS256` nếu đối tác dùng RSASSA-PSS. Ví dụ phía đối tác:

~~~bash
ts=$(date +%s); path='/api/orders?page=2'; body='{"amount":10}'
hash=$(printf '%s' "$body" | openssl dgst -sha256 -r | cut -d' ' -f1)
sig=$(printf 'POST\n%s\n%s\n%s\n%s' "$path" "$ts" partner-a "$hash" | openssl dgst -sha256 -sign partner-a.key | base64 -w0)
curl -X POST -d "$body" -H "X-Client-Id: partner-a" -H "X-Timestamp: $ts" -H "X-Signature: $sig" "https://gateway.example.com$path"
~~~

Gateway từ chối (401) khi chữ ký sai (kể cả sai method, path, query hoặc body), client không có hoặc đã tắt, timestamp lệch quá `MaxClockSkewSeconds` (mặc định 300 giây), body lớn hơn `MaxBodyBytes` (mặc định 128 KiB, tối đa 10 MiB; gateway phải đọc body vào bộ nhớ để băm nên bộ nhớ xấu nhất là `RateLimiting:Local:InstanceConcurrencyLimit` × `MaxBodyBytes`, mặc định 1024 × 128 KiB = 128 MiB, chỉ nâng khi pod đủ bộ nhớ), hoặc chữ ký đã được dùng rồi. Chống replay: mỗi chữ ký chỉ được chấp nhận một lần trong 2 × `MaxClockSkewSeconds`. Có Redis (`Redis:ConnectionString`) thì mọi pod dùng chung bộ nhớ này; không có Redis hoặc Redis đang lỗi thì mỗi pod tự nhớ, vẫn chặn replay vào cùng pod nhưng chưa chặn được replay sang pod khác; vì vậy ở Production bật `RejectReplays` mà thiếu `Redis:ConnectionString` thì gateway không khởi động. Bộ nhớ của pod giữ tối đa `ReplayMemoryEntries` chữ ký (mặc định 500.000, ước tính khoảng 0,5 KB mỗi mục; cần cân lưu lượng ký mỗi giây × `2 × MaxClockSkewSeconds` với giới hạn bộ nhớ của pod). Đầy thì không nhớ thêm cho đến khi mục cũ hết hạn và counter `gateway_authentication_signature_replay_memory_full_total` tăng. Tắt bằng `RejectReplays: false`. Client ký xong mang claim giống client API key (`sub`, `scope`, `role`, `tenant_id`, issuer `urn:core-gateway:signature`) nên dùng chung quota và header downstream; ba header chữ ký không bao giờ được chuyển xuống backend. Lưu ý: path được ký là dạng URL-encoded như Kestrel nhận, nên proxy phía trước viết lại URL sẽ làm chữ ký lệch.

**Các trường hợp cấu hình** (mỗi file trong `deploy/examples/authentication/` là một cấu hình hoàn chỉnh, được test nạp qua validator thật nên không lỗi thời; bản biến môi trường/secret ở `deploy/examples/secrets.example.yaml`):

| Trường hợp | File mẫu | Ghi chú |
|---|---|---|
| Một IdP duy nhất | `single-idp.json` | `Jwt:Authority` + `Jwt:Audience`, `Providers` để trống |
| SSO (OIDC) | `sso.json` | Provider có `Authority`, khóa tự cập nhật qua JWKS của IdP |
| Đối tác ký JWT bằng secret chung | `partner-hmac.json` | `SigningKey` + `HS256/384/512`, tối thiểu 32/48/64 byte |
| Đối tác ký JWT bằng khóa công khai | `partner-public-key.json` | `PublicKeyFile`: JWKS, PEM RSA/EC, hoặc certificate |
| Máy gọi máy bằng API key | `api-key.json` | `ApiKeys`, chỉ lưu SHA-256; client tắt bằng `Enabled: false` |
| Bên thứ ba ký request bằng RSA | `signature.json` | `Signature`: mỗi client một `PublicKeyFile`, không cần IdP |
| Chỉ đối tác, không có IdP | `partner-only.json` | API key và chữ ký, không cấu hình `Jwt` |
| Tất cả cùng lúc | `combined.json` | SSO + đối tác + API key; `iss` chọn provider, `X-Api-Key` chọn API key |
| Dev, không xác thực | `development-disabled.json` | Production không cho phép (`Authentication:Enabled` bắt buộc `true`) |

JWT chỉ bắt buộc khi không có luồng nào khác: nếu `ApiKeys` hoặc `Signature` đang bật thì có thể bỏ hẳn nhánh `Jwt` (cấu hình dở dang, ví dụ chỉ có `Audience`, vẫn báo lỗi).

Cấu hình xác thực chia ba nhánh giống cấu trúc code: `Authentication:Jwt:*` (env var `Authentication__Jwt__*`), `Authentication:ApiKeys:*` và `Authentication:Signature:*`, còn `Authentication:Enabled` là công tắc chung. Key phẳng cũ (`Authentication:Authority`, `Authentication:Providers`…) không còn được đọc: production thiếu `Audience`/`Authority` sẽ không khởi động, nên khi nâng cấp phải đổi tên env var trong secret `core-gateway-jwt-providers` và `core-gateway-auth`.

## Rate limit

Tài liệu đầy đủ: [docs/rate-limiting.md](docs/rate-limiting.md).

- **Quota chung (Redis) dùng cửa sổ trượt** (sliding window counter): mức dùng được ước lượng bằng `số request của cửa sổ trước × phần cửa sổ trước còn nằm trong cửa sổ trượt + số request của cửa sổ hiện tại`. Nhờ đó không còn trường hợp dùng gần gấp đôi hạn mức ngay sau ranh giới giữa hai cửa sổ như cửa sổ cố định, mà mỗi partition vẫn chỉ tốn một key. Mỗi lần gọi là một batch Redis (`INCR` + `EXPIRE` cửa sổ hiện tại, `GET` cửa sổ trước), request bị từ chối được hoàn lại bằng `DECR`. Ranh giới cửa sổ tính theo đồng hồ UTC của từng pod nên các node cần đồng bộ NTP; `Retry-After` tính từ cùng số liệu đó.
- **Bộ đếm local thay thế cho client cũng là cửa sổ trượt** (cap IP cho người chưa xác thực, quota dự phòng khi Redis lỗi). Riêng cầu dao quá tải theo service/instance giữ cửa sổ cố định vì chúng bảo vệ CPU chứ không chia công bằng cho client.
- **Khi Redis lỗi**: theo `RateLimiting:Distributed:Fallback`. `UseLocalFallbackWhenStoreUnavailable=false` trả 503 kèm `Retry-After` đúng bằng thời gian còn lại của cooldown (`Redis:FailureCooldownSeconds`), để client không dồn vào một store đang chết.
- **Số pod khi Redis lỗi**: mỗi pod ghi heartbeat vào Redis (`{KeyPrefix}:ratelimit:instances`, mỗi `Fallback:HeartbeatSeconds`) và đếm số pod còn sống. Khi Redis chết, quota dự phòng của mỗi pod là `1 / số pod` lần cuối đếm được (`Fallback:AutoScaleRatio=true`); chưa đếm được thì dùng `Fallback:LocalRatio`. Gauge `gateway_ratelimit_instances_active`.
- **Header `X-RateLimit-Limit/Remaining/Reset`**: có trên cả response thành công lẫn 429, lấy từ quota chung (Redis) đang gần cạn nhất trong request. `Reset` là số giây đến hết cửa sổ hiện tại (khi bị 429 bằng `Retry-After`). Không có khi Redis lỗi hoặc chỉ giới hạn local. Khi bật CORS, gateway cho trình duyệt đọc `X-Correlation-ID`, `Retry-After` và ba header `X-RateLimit-*`.

Metric (OTel, tên Prometheus): `gateway_ratelimit_distributed_{allowed,rejected}_total{partition_type}`, `gateway_ratelimit_redis_unavailable_total` (mỗi lần Redis không dùng được), `gateway_ratelimit_distributed_fail_open_total` (quyết định được đưa ra khi Redis lỗi; sau đó quota dự phòng theo pod vẫn áp dụng nếu Redis đã cấu hình), `gateway_ratelimit_local_fallback_total{result}`, `gateway_ratelimit_route_decisions_total{policy,result,store}` (`result`: `allowed`, `rejected`, `unavailable`), `gateway_ratelimit_anonymous_rejected_total`, `gateway_ratelimit_redis_duration_milliseconds`. Alert `GatewaySharedQuotaFailOpen` báo khi quota chung đang chạy ở chế độ dự phòng.

## Giám sát xác thực

Một counter chung cho mọi loại credential: `gateway_authentication_attempts_total{scheme, result, reason}`. `scheme` là `jwt`, `api_key` hoặc `signature`; `result` là `success` hoặc `failure`; `reason` (chỉ khi thất bại) thuộc một tập cố định, không bao giờ là chữ do người gọi gửi: `malformed`, `invalid_key`, `unknown_client`, `invalid_signature`, `expired`, `body_too_large`, `replayed` (API key và chữ ký); `expired`, `invalid_signature`, `invalid_audience`, `invalid_issuer`, `invalid_token`, `idp_unavailable` (JWT). Ví dụ "đối tác nào ký sai": `sum by (reason) (rate(gateway_authentication_attempts_total{scheme="signature",result="failure"}[5m]))`. Mỗi lần thất bại của API key và chữ ký còn ghi một dòng log mức Information có lý do (và client id nếu hợp lệ), không bao giờ ghi key hay chữ ký. Request không xác thực được bị giới hạn theo IP bởi `RateLimiting:Local:Anonymous` (metric `gateway_ratelimit_anonymous_rejected_total`), kể cả khi kèm header credential sai.

## Redis

Redis là một dịch vụ dùng chung (`IRedisConnection`), không thuộc riêng tính năng nào: quota chung của rate limit và bộ nhớ chống replay của chữ ký đều đi qua đúng một kết nối, nên thêm tính năng mới cần Redis thì chỉ gọi `AddGatewayRedis(configuration)`. Cấu hình nằm ở nhánh riêng, không phụ thuộc rate limit:

| Key | Mặc định | Ý nghĩa |
|---|---|---|
| `Redis:ConnectionString` | trống | Có giá trị thì Redis được dùng; trống thì mọi tính năng chạy theo từng pod |
| `Redis:KeyPrefix` | `gateway` | Tiền tố mọi key; mỗi tính năng thêm đoạn riêng sau nó (`:ratelimit`, `:signature-replay`) |
| `Redis:TimeoutMilliseconds` | 250 | Timeout mỗi lệnh |
| `Redis:ConnectTimeoutMilliseconds` | 2000 | Timeout khi kết nối |
| `Redis:FailureCooldownSeconds` | 5 | Sau khi lỗi, tránh gọi Redis bao lâu trước khi thử lại |

Mọi lỗi Redis (không kết nối được, timeout, lệnh trả lỗi) được gộp thành một loại `RedisUnavailableException`, nên mỗi tính năng chỉ cần quyết định "khi Redis không dùng được thì làm gì" (rate limit: `RateLimiting:Distributed:Fallback`; chống replay: chỉ nhớ theo từng pod). `RateLimiting:Distributed:Enabled` chỉ bật việc dùng Redis cho quota chung, và ở Production nếu bật mà thiếu `Redis:ConnectionString` thì gateway không khởi động (nếu không, mỗi pod đếm riêng và quota nhân lên theo số pod mà không ai hay); cluster không có Redis thì đặt `RateLimiting__Distributed__Enabled=false` trong `settings.env`. Key quota có dạng `<prefix>:ratelimit:sw:<loại>:<hash>:<cửa sổ ms>` (`sw` = sliding window); bản dùng cửa sổ cố định trước đó dùng key khác nên không đụng nhau, bộ đếm chỉ bắt đầu lại từ đầu một lần lúc nâng cấp.

## Luồng request

~~~text
Ingress TLS → forwarded headers → correlation → error handling → host filtering → security headers/logging
  → routing → management port → giới hạn body → timeout → CORS → concurrency + cầu dao local theo service/instance (trước xác thực)
  → JWT, API key hoặc chữ ký → cap IP cho người chưa xác thực → quota route → quota client (Redis) → yêu cầu đã xác thực → transforms
  → retry (chỉ request idempotent) → load balancing → passive health → backend
~~~

## Scale-out

- **Stateless**: không có session; quota user/IP/tenant/API key dùng chung qua Redis (`RateLimiting__Distributed__Enabled=true` trong `settings.env`; Redis cấu hình riêng ở nhánh `Redis`, connection string `Redis__ConnectionString` lấy từ secret `core-gateway-redis`). Nếu không có Redis, quota sẽ nhân lên theo số pod.
- **Cầu dao local** (`RateLimiting:Local`) luôn tính theo từng pod để bảo vệ CPU, kể cả khi Redis lỗi. Giới hạn được đo trong `tests/load/results`.
- **HPA quản lý số pod**: Deployment không khai báo `replicas`. Mặc định HPA scale theo CPU (min 3, max 10), có PDB `minAvailable: 2`, rolling update `maxUnavailable: 0`.
- **Scale theo request đang xử lý** (tùy chọn): `deploy/kubernetes/components/inflight-autoscaling`. Đây là tín hiệu thứ hai khi backend chậm làm request dồn lại mà CPU vẫn thấp. Cần prometheus-adapter; điều kiện ghi trong file.
- **Thêm service mới chỉ sửa `reverseproxy.json`**: Ingress chuyển toàn bộ `/` vào gateway, còn health chạy trên port quản trị 8081 mà Service không expose.

## Chạy local

~~~powershell
# Mỗi lệnh một terminal; Development tắt authentication để dùng mock API.
dotnet run --project services/User.Api --launch-profile http
dotnet run --project services/Order.Api --launch-profile http
dotnet run --project services/Product.Api --launch-profile http
dotnet run --project src/Core.Gateway.Api --launch-profile http
~~~

Gọi `/api/users`, `/api/orders`, `/api/products`; các alias `/users`, `/orders`, `/products` được rewrite sang `/api/...`. Mock API chỉ chạy ở Development. Mọi môi trường khác bắt buộc bật JWT, khai báo trusted proxy và giới hạn host.

## Kiểm tra

~~~powershell
dotnet format Core.Gateway.sln --verify-no-changes
dotnet build Core.Gateway.sln -c Release
dotnet test Core.Gateway.sln -c Release --no-build
pwsh -File tests/test-redis.ps1               # chạy thêm các test cần Redis thật (Docker)
pwsh -File tests/smoke-development.ps1        # 3 mock API + gateway qua HTTP thật
python tests/validate_manifests.py            # contract Kustomize (kubectl)
python tests/validate_alerts.py               # PrometheusRule + promtool (Docker)
pwsh -File deploy/validate-production.ps1     # render và chặn placeholder trước khi deploy
~~~

Load test và drill sự cố: `tests/load/run-capacity.ps1`, `tests/load/run-drills.ps1`. Mỗi alert trong `deploy/observability/prometheus-rules.yaml` có annotation `action` mô tả cách xử lý.
