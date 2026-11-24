# Prototype Instructions

用户已选择第 3 张概念图（`design/reference.png`）：深蓝灰底、青绿色国内链路、琥珀色故障提示、顶部导航与可视化链路工作台。后续保持这一布局与视觉方向。

用户要求直连辅助程序作为分流助手自带组件交付：放在应用同目录或固定子目录，由助手自动定位、管理；普通使用流程不要求手填/复制辅助程序路径，也不要求单独下载辅助程序。保留独立进程与 VPN 排除边界，主界面和 Clash 内核留在 VPN 内。当前为前端原型，组件打包与系统接入尚待桌面版本实现，不能宣称已打包二进制。

Run the local server yourself and open the preview in the browser available to this environment. Do not give the user server-start instructions when you can run it.

Before making substantial visual changes, use the Product Design plugin's `get-context` skill when the visual source is unclear or no longer matches the current goal. When the user gives durable prototype-specific design feedback, preferences, or decisions, record them in `AGENTS.md`.

When implementing from a selected generated mock, treat that image as the source of truth for layout, component anatomy, density, spacing, color, typography, visible content, and hierarchy.

Build app UI in `src/`. Keep `.openai/hosting.json`, `worker/index.js`, `scripts/prepare-sites-build.mjs`, and `tests/sites-worker.test.mjs` intact so the same local prototype can be handed to Sites. Before a Sites handoff, run `npm run build` and `npm run test:sites`; the build must leave `dist/client/index.html`, `dist/server/index.js`, and `dist/.openai/hosting.json`.
