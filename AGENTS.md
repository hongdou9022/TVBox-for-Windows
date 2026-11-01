# Windows 客户端开发入口

- 每次回复以「云」开头。
- 开工前读取 [工作区协作入口](../AGENTS.md) 和 [项目规则](../Box/docs/PROJECT_RULES.md)，按其中的 Sol / Luna 分工与验证要求执行。
- 接着读取 [README](README.md)、[运行时契约](docs/development/RUNTIME-CONTRACT.md) 和 [接口契约](docs/development/CONTRACTS.md)。
- 本端连接 Workers 服务，改客户端接口时检查 `../cloudtv_js` 和 `../backend`；维持共享 API 的字段、鉴权和错误语义。
- 不自行恢复已移除的 JAR / Python 侧车；依赖版本以当前 `.csproj` 为准，文档记录有漂移时先核对实现。
