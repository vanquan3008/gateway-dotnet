# Rate Limiting

```text
Options/                         Config và nested validation
Shared/                          Trusted partition resolution và HTTP responses
Local/                           Native concurrency/rate protection cho pod
Distributed/Interfaces/          Contract quota store
Distributed/                     Redis quota, middleware, metrics và readiness
GatewayRateLimitingExtensions.cs  DI / native RateLimiter registration
```

Local và Distributed là hai lớp độc lập. Local chỉ đọc LocalRateLimitingOptions, không đọc Redis status hoặc shared quota. Shared không có marker quota đã acquire. Redis store không gọi local limiter hoặc quyết định FailOpen; middleware phân biệt Allowed/Rejected/Unavailable và áp failure policy.

Concurrency chạy trước JWT và giữ lease xuyên pipeline; native local fixed-window chạy sau JWT, trước Redis. DI sở hữu singleton fixed-window chain và concurrency middleware, dispose cả hai khi container đóng; native middleware chỉ sử dụng GlobalLimiter đã đăng ký. Health endpoints bỏ qua cả hai lớp.

Redis dùng một multiplexer lâu dài, atomic Lua/TTL, timeout/cooldown, một recovery probe và failure version. Không tạo connection hoặc retry command mỗi request. Shared quotas chỉ tồn tại trên Redis: FailOpen bỏ qua quota unavailable, FailClosed trả 503.

Xem [cấu hình và migration](../../../../docs/rate-limiting.md) và [audit kiến trúc](../../../../docs/rate-limiting-architecture-review.md).
