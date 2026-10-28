# 待做 / TODO

更新时间：2026-10-28

## 后续可能需要

- **安装包**：当前仅提供便携 ZIP（`dist`）。后续需要 Inno Setup/MSIX 安装包、代码签名与 SmartScreen 说明。
- **EPG / 回看**：CloudTV IPTV 接口（`/api/iptv/{id}/channels`）只下发频道与播放地址，没有 XMLTV/catchup 字段；需后端补 EPG/回看数据后接入 `EpgService` 与直播回看。频道收藏已可用。
- **多语言 / 主题**、日志诊断导出、媒体键 / SMTC。
- **影视库分类浏览**：CloudTV 34 个库、1154 个分类未暴露；用户明确暂不做，首页保留豆瓣 7 分类。

## 明确不做

- 远程推送接收端（手机投屏到 Windows）。
- 自动更新。
