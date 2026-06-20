# YuraSub

YuraSub 是一个 Windows 优先的本地透明桌面字幕浮层。浏览器端 Tampermonkey 脚本从 asmr.one / Kikoeru 页面读取当前字幕，并通过本机 WebSocket 推送给 PySide6 桌面窗口。

## 仓库结构

```text
assets/
  player-html/     收集到的网站播放器 HTML 快照
  docs/            推荐档案建设相关资料
  readme-images/   README 使用的图片
src/
  python/          Python 桌面端和本地服务代码
  dotnet/          预留 .NET Native AOT 代码
  tampermonkey/    Tampermonkey 浏览器脚本
tests/             Python 回归测试和本地 mock 页面
```

根目录不保留 `main.py` 入口；运行、测试和打包统一通过 pixi task 执行。

## 运行

```powershell
pixi run start
```

默认监听：

```text
ws://127.0.0.1:8765
http://127.0.0.1:8766
```

启动后只显示字幕层。字幕层顶部带一排内嵌控制按钮，默认可拖动/可缩放，锁定后会点击穿透；需要移动或调样式时，从托盘点"解锁拖动/显示控制"即可救回。

## 设置持久化

程序退出时自动保存当前状态到 `config.json`，下次启动自动恢复。便携版不会写入注册表、`%APPDATA%` 或用户目录。

保存的内容包括：

- 窗口位置和尺寸
- 锁定/点击穿透状态
- 字体、正文字号、翻译字号
- 正文颜色/透明度
- 描边颜色/宽度/透明度
- 图标/控件颜色（工具栏按钮、时间文本、进度条、拖拽角标）
- WebSocket 端口、HTTP fallback 端口

### 默认配置路径

| 运行方式 | 配置文件位置 |
|---------|------------|
| `pixi run start`（开发模式） | 仓库根目录 `config.json` |
| `YuraSub.exe`（打包后） | exe 同目录 `config.json` |

开发模式下自动通过 `pixi.toml` 定位仓库根目录，不会写入 `.pixi/envs/`。

### 配置优先级

1. `--config path/to/settings.json`（最高）
2. 环境变量 `YURASUB_CONFIG`
3. 默认路径（见上表）

### 配置文件示例

```json
{
  "schemaVersion": 1,
  "server": {
    "websocketPort": 8765,
    "httpPort": 8766
  },
  "window": {
    "x": 200,
    "y": 780,
    "width": 1100,
    "height": 180,
    "clickThrough": false
  },
  "style": {
    "fontFamily": "Microsoft YaHei UI",
    "fontSize": 34,
    "translationFontSize": 24,
    "textColor": "#ffffff",
    "textOpacity": 100,
    "outlineColor": "#101522",
    "outlineWidth": 4,
    "outlineOpacity": 100,
    "controlColor": "#f5fff8e6",
    "controlOpacity": 90
  }
}
```

手动编辑 JSON 后，下次启动生效。也可以在托盘菜单中选择"恢复默认设置"。

### 端口配置

默认端口：WebSocket `8765`，HTTP fallback `8766`。修改方式（优先级从高到低）：

1. **命令行参数**（最高优先级）：
   ```powershell
   pixi run start --port 9001 --http-port 9002
   ```
2. **配置文件**：编辑 `config.json` 中的 `server.websocketPort` 和 `server.httpPort`。
3. **内置默认值**：`8765` / `8766`。

浏览器 userscript 默认仍连接 `8765`，修改端口后需同步更新脚本中的 `CONFIG.endpoint` 和 `CONFIG.httpEndpoint`。

浏览器脚本连接后会按脚本里的 `geometry`、`style` 提供初始配置。桌面端默认不让脚本覆盖本地锁定状态；你在字幕层顶部手动改过的锁定状态、位置尺寸、样式不会被下一条字幕刷新覆盖。

## 字幕层控制

字幕层顶部按钮提供：

- `A+` / `A-`: 调整正文字号和翻译字号。
- 上一首 / 下一首：通过 WebSocket 回传给浏览器，优先点击页面播放器按钮，找不到按钮时尝试点击当前播放列表的相邻音频项，再回退到媒体按键事件。
- 进度条：显示当前播放位置，右侧显示 `当前时间 / 总时间`；拖动后会发送 `seekTo` 跳转浏览器播放器。
- 播放暂停：通过 WebSocket 回传给浏览器，按钮会根据浏览器回传的暂停状态在 `▶` / `Ⅱ` 间切换。
- 样式按钮：展开一行透明内嵌样式面板，调整字体、正文字号、正文颜色、描边颜色、图标颜色；颜色透明度在取色框 alpha 通道里选。图标颜色控制工具栏按钮、时间文本、进度条和拖拽角标的颜色，方便在浅色背景下保持可见。
- `×`: 清空字幕。
- 锁定：隐藏控制条并让字幕层点击穿透。

托盘菜单只保留救援入口：解锁拖动/显示控制、锁定字幕、清空字幕、退出。

## Tampermonkey

安装 `src/tampermonkey/yurasub.user.js`。脚本默认匹配 `https://www.asmr.one/`、`https://asmr.one/`、`https://kikoeru.moear.de/` 和本地 Kikoeru 地址，然后在脚本顶部修改 `CONFIG`：

- `endpoint`: PySide6 端监听地址。
- `httpEndpoint`: WebSocket 连不上时使用的 HTTP fallback 地址；脚本会 POST 字幕并轮询 GUI 发出的媒体命令。
- `clickThrough`: 兼容字段；桌面端默认不再让脚本覆盖本地锁定状态。
- `geometry`: 浮层尺寸与屏幕锚点。
- `style`: 初始字体、正文/翻译字号、描边、阴影、RGBA 颜色、透明度。颜色可以写 `#rrggbbaa` 或 `rgba(255,255,255,0.8)`。
- `splitMultilineTranslation`: 多行字幕拆分开关；默认把第一行当正文，后续行当翻译。
- `primarySelectors`: 稳定字幕节点。只要这些节点存在，即使当前为空也不会 fallback 到页面其他文字。
- `selectors`: 回退字幕节点选择器。asmr.one 当前优先使用 `#lyric`，本地 Kikoeru 可以按实际页面追加选择器。

脚本也提供调试入口：

```javascript
window.YuraSubDebug.inspect()
window.YuraSubDebug.current()
window.YuraSubDebug.status()
window.YuraSubDebug.push()
```

`inspect()` 会列出当前页面检测到的候选字幕节点，`current()` 会显示当前将要推送的字幕来源，方便校准 Kikoeru 本地部署页面的 class/id。

为避免字幕空档误抓作品标题，脚本会优先锁定 `#lyric` 等稳定节点；桌面端也会过滤类似 `UZMR` 这种短全大写页面噪声。

浮层按钮会向脚本发送 `mediaCommand`：`previousTrack`、`playPause`、`nextTrack`、`seekTo`。Kikoeru 会优先点击原播放器里的 `skip_previous` / `skip_next` / `pause` / `play_arrow` Material Icons；如果站点按钮选择器不匹配，可以在脚本里的 `PREVIOUS_SELECTORS`、`PLAY_PAUSE_SELECTORS`、`NEXT_SELECTORS` 追加本地 Kikoeru 页面的真实按钮选择器。

## WebSocket Payload

浏览器侧发送 JSON：

```json
{
  "type": "subtitle",
  "text": "字幕正文",
  "translation": "可选翻译",
  "media": { "currentTime": 32.5, "duration": 184.2, "paused": false, "src": "track.mp3" },
  "clickThrough": false,
  "geometry": { "width": 1100, "height": 180, "anchor": "bottom", "marginBottom": 80 },
  "style": {
    "fontFamily": "Microsoft YaHei UI",
    "fontSize": 36,
    "translationFontSize": 24,
    "textColor": "#ffffff",
    "textOpacity": 100,
    "outlineColor": "#101522",
    "outlineWidth": 4,
    "outlineOpacity": 100,
    "backgroundColor": "#00000000",
    "backgroundOpacity": 0
  }
}
```

清空字幕：

```json
{ "type": "clear" }
```

## 打包

```powershell
pixi run build
```

输出文件在 `dist/YuraSub.exe`。构建任务使用 Nuitka onefile 和 PySide6 插件，从 `src/python` 下的 `yurasub.__main__` 入口打包，不包含额外 GUI 配置界面。

## 测试

```powershell
pixi run test
```

测试任务使用 pixi 环境内的 pytest。

## 回归测试页

`tests/fixtures/kikoeru_mock.html` 是本地 mock 页面，包含双语 `#lyric`、上一首/播放/下一首按钮和一个噪声 `.subtitle`。它用于验证空字幕时不会把 `UZMR` 当作字幕推送，并验证多行字幕会拆成正文/翻译。
