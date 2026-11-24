import { useEffect, useRef, useState } from "react";
import { ReactFlow, Handle, Position, MarkerType } from "@xyflow/react";
import {
  ShareNetwork,
  ShieldCheck,
  CaretDown,
  Warning,
  ArrowSquareOut,
  SquaresFour,
  Stack,
  Laptop,
  RocketLaunch,
  WifiHigh,
  Globe,
  CheckCircle,
  Clock,
  ArrowsClockwise,
  Play,
  Pause,
  ArrowRight,
  ArrowLeft,
  Check,
  X,
  DownloadSimple,
  Eye,
  EyeSlash,
  Info,
  Power,
  ListChecks,
  FileCode,
} from "@phosphor-icons/react";
import "@xyflow/react/dist/style.css";
import { createProfile, formErrors } from "./configuration.mjs";
import mullvadTemplate from "./templates/mullvad.yaml?raw";
import windscribeTemplate from "./templates/windscribe.yaml?raw";
import mullvadDns from "./templates/mullvad-dns.js?raw";
import windscribeDns from "./templates/windscribe-dns.js?raw";

const templates = { Mullvad: mullvadTemplate, Windscribe: windscribeTemplate };
const dnsScripts = { Mullvad: mullvadDns, Windscribe: windscribeDns };
const steps = ["检查环境", "VPN 设置", "生成配置", "检测与启用"];
const initialValues = {
  ip: "",
  port: "61053",
  username: "",
  password: "",
  mixedPort: "7897",
};
const guideLinks = {
  Mullvad: "https://mullvad.net/en/help/split-tunneling-with-the-mullvad-app",
  Windscribe:
    "https://windscribe.com/knowledge-base/articles/what-is-the-proxy-gateway-feature-and-how-to-use-it",
};

function saveFile(name, content, mime = "text/plain;charset=utf-8") {
  const url = URL.createObjectURL(new Blob([content], { type: mime }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = name;
  anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

function RouteNode({ data }) {
  const Icon = data.icon;
  return (
    <div
      className={`route-node ${data.tone || "neutral"} ${data.tower ? "tower" : ""} ${data.pill ? "route-pill" : ""}`}
    >
      <Handle type="target" position={Position.Left} />
      <Icon
        size={data.tower ? 42 : 30}
        weight={data.pill ? "fill" : "regular"}
        aria-hidden="true"
      />
      <span>{data.label}</span>
      <Handle type="source" position={Position.Right} />
    </div>
  );
}
const nodeTypes = { route: RouteNode };

function RouteDiagram({ provider, scenario, busy }) {
  const [instance, setInstance] = useState(null);
  const canvasRef = useRef(null);
  useEffect(() => {
    if (!instance) return;
    const observer = new ResizeObserver(() =>
      instance.fitView({ padding: 0.015 }),
    );
    observer.observe(canvasRef.current);
    return () => observer.disconnect();
  }, [instance]);
  const stopped = scenario === "stopped";
  const healthy = scenario === "healthy";
  const overseasTone = stopped ? "neutral" : healthy ? "good" : "attention";
  const domesticTone = stopped ? "neutral" : "good";
  const statusText = busy
    ? "检测中"
    : stopped
      ? "未启用"
      : healthy
        ? "正常"
        : scenario === "vpn-offline"
          ? "等待 VPN 连接"
          : "等待更新地址";
  const spec = [
    [
      "apps",
      0,
      88,
      162,
      148,
      SquaresFour,
      "遵循系统代理\n的应用",
      "neutral",
      true,
    ],
    ["clash", 238, 96, 158, 144, Stack, "Clash Verge", "neutral", true],
    ["domestic", 486, 50, 140, 62, Laptop, "国内", domesticTone],
    ["helper", 680, 50, 238, 62, RocketLaunch, "内置直连组件", domesticTone],
    ["broadband", 976, 50, 174, 62, WifiHigh, "本地宽带", domesticTone],
    [
      "domestic-status",
      1190,
      59,
      134,
      44,
      busy ? ArrowsClockwise : CheckCircle,
      busy ? "检测中" : stopped ? "未启用" : "正常",
      domesticTone,
      false,
      true,
    ],
    ["overseas", 486, 210, 140, 62, Globe, "海外", overseasTone],
    [
      "gateway",
      680,
      210,
      238,
      62,
      ShieldCheck,
      `${provider} ${provider === "Mullvad" ? "SOCKS" : "网关"}`,
      overseasTone,
    ],
    ["vpn", 976, 210, 174, 62, ShieldCheck, "VPN", overseasTone],
    [
      "overseas-status",
      1190,
      219,
      198,
      44,
      busy ? ArrowsClockwise : healthy ? CheckCircle : Clock,
      statusText,
      overseasTone,
      false,
      true,
    ],
  ];
  const nodes = spec.map(
    ([id, x, y, width, height, icon, label, tone, tower, pill]) => ({
      id,
      type: "route",
      position: { x, y },
      style: { width, height },
      data: { icon, label, tone, tower, pill },
      draggable: false,
      selectable: false,
      focusable: false,
    }),
  );
  const connections = [
    ["apps", "clash", "neutral"],
    ["clash", "domestic", domesticTone],
    ["domestic", "helper", domesticTone],
    ["helper", "broadband", domesticTone],
    ["clash", "overseas", overseasTone],
    ["overseas", "gateway", overseasTone],
    ["gateway", "vpn", overseasTone],
  ];
  const colors = { good: "#7ce0c2", attention: "#f9ba40", neutral: "#8caebc" };
  const edges = connections.map(([source, target, tone]) => ({
    id: `${source}-${target}`,
    source,
    target,
    type: "default",
    markerEnd: {
      type: MarkerType.ArrowClosed,
      color: colors[tone],
      width: 14,
      height: 14,
    },
    style: {
      stroke: colors[tone],
      strokeWidth: 2.4,
      strokeDasharray: source === "gateway" && !healthy ? "8 6" : undefined,
    },
  }));
  return (
    <figure
      className="route-figure"
      aria-label={`访问链路示意：国内经内置直连组件到本地宽带；海外经 ${provider} 网关到 VPN。${statusText}。`}
    >
      <div className="route-scroll">
        <div className="route-canvas" ref={canvasRef}>
          <ReactFlow
            key={provider}
            onInit={setInstance}
            nodes={nodes}
            edges={edges}
            nodeTypes={nodeTypes}
            fitView
            fitViewOptions={{ padding: 0.015 }}
            minZoom={0.4}
            maxZoom={1.1}
            nodesConnectable={false}
            nodesDraggable={false}
            elementsSelectable={false}
            panOnDrag={false}
            zoomOnScroll={false}
            zoomOnPinch={false}
            zoomOnDoubleClick={false}
            preventScrolling={false}
            colorMode="dark"
            aria-label="国内与海外访问路径"
          />
        </div>
      </div>
      <figcaption className="mobile-hint">左右滑动查看完整链路</figcaption>
    </figure>
  );
}

function Dialog({ title, children, onClose, wide = false }) {
  const ref = useRef(null);
  useEffect(() => {
    ref.current.showModal();
  }, []);
  return (
    <dialog
      ref={ref}
      className={`dialog ${wide ? "wide-dialog" : ""}`}
      aria-labelledby="dialog-title"
      onCancel={onClose}
      onClick={(event) => {
        if (event.target === event.currentTarget) onClose();
      }}
    >
      <div className="dialog-heading">
        <h2 id="dialog-title">{title}</h2>
        <button className="icon-button" aria-label="关闭弹窗" onClick={onClose}>
          <X size={24} />
        </button>
      </div>
      {children}
    </dialog>
  );
}

function Field({
  label,
  name,
  values,
  setValues,
  errors = {},
  type = "text",
  placeholder,
  hint,
}) {
  const [reveal, setReveal] = useState(false);
  return (
    <div className="field">
      <label htmlFor={`field-${name}`}>{label}</label>
      <div className="input-wrap">
        <input
          id={`field-${name}`}
          name={name}
          value={values[name]}
          type={type === "password" && reveal ? "text" : type}
          onChange={(event) =>
            setValues((previous) => ({
              ...previous,
              [name]: event.target.value,
            }))
          }
          placeholder={placeholder}
          autoComplete="off"
          spellCheck={false}
          inputMode={
            name === "port" || name === "mixedPort" ? "numeric" : undefined
          }
          aria-invalid={Boolean(errors[name])}
          aria-describedby={
            errors[name] ? `error-${name}` : hint ? `hint-${name}` : undefined
          }
        />
        {type === "password" && (
          <button
            type="button"
            className="input-icon"
            onClick={() => setReveal(!reveal)}
            aria-label={reveal ? "隐藏网关密码" : "显示网关密码"}
          >
            {reveal ? <EyeSlash size={22} /> : <Eye size={22} />}
          </button>
        )}
      </div>
      {errors[name] ? (
        <span className="field-error" id={`error-${name}`}>
          {errors[name]}
        </span>
      ) : hint ? (
        <span className="field-hint" id={`hint-${name}`}>
          {hint}
        </span>
      ) : null}
    </div>
  );
}

function ProviderGuide({ provider }) {
  return (
    <div className="provider-guide">
      <h3>在 {provider} 中完成设置</h3>
      <ol className="instruction-list">
        <li>
          <span>1</span>
          <div>
            <strong>连接官方 VPN</strong>
            <p>在官方客户端登录并选择出口。</p>
          </div>
        </li>
        <li>
          <span>2</span>
          <div>
            <strong>
              {provider === "Mullvad"
                ? "由助手配置应用分流"
                : "允许内置组件直连"}
            </strong>
            <p>
              {provider === "Windscribe"
                ? "分流选择 Exclusive / 排除模式。由助手定位内置组件，首次按指引确认排除项。"
                : "助手自动定位内置组件，并通过官方接口添加排除项，无需选择文件或填写路径。"}
            </p>
          </div>
        </li>
        <li>
          <span>3</span>
          <div>
            <strong>
              {provider === "Windscribe"
                ? "开启认证 SOCKS 网关"
                : "允许本地网络访问"}
            </strong>
            <p>
              {provider === "Windscribe"
                ? "开启 Require Authentication 和 Only while connected，并使用固定端口。"
                : "主 Clash 内核继续留在 VPN 内，使用 Clash 服务模式。"}
            </p>
          </div>
        </li>
      </ol>
      <div className="bundled-component">
        <div>
          <RocketLaunch size={22} aria-hidden="true" />
          <strong>内置直连组件</strong>
          <span className="badge good">随应用提供</span>
        </div>
        <p>由分流助手自动准备、定位和管理，无需单独下载程序或配置路径。</p>
      </div>
      <p className="small-note">
        仅将内置直连组件排除出 VPN，主界面与 Clash 内核保持在 VPN 内。
        {provider === "Windscribe" && "Windscribe.exe 也保持在 VPN 内。"}
      </p>
      <a
        className="text-link"
        href={guideLinks[provider]}
        target="_blank"
        rel="noreferrer"
      >
        查看官方设置说明 <ArrowSquareOut size={17} />
      </a>
    </div>
  );
}

export function App() {
  const [tab, setTab] = useState("overview");
  const [provider, setProvider] = useState("Windscribe");
  const [menuOpen, setMenuOpen] = useState(false);
  const [scenario, setScenario] = useState("gateway-changed");
  const [busy, setBusy] = useState(false);
  const [dialog, setDialog] = useState(null);
  const [step, setStep] = useState(1);
  const [settingsSection, setSettingsSection] = useState("connection");
  const [values, setValues] = useState(initialValues);
  const [errors, setErrors] = useState({});
  const [confirmed, setConfirmed] = useState(false);
  const [startup, setStartup] = useState({
    login: true,
    enable: false,
    tray: true,
  });
  const [toast, setToast] = useState("");
  const operationTimer = useRef(null);
  useEffect(() => () => clearTimeout(operationTimer.current), []);
  useEffect(() => {
    if (!toast) return;
    const timer = setTimeout(() => setToast(""), 4500);
    return () => clearTimeout(timer);
  }, [toast]);

  const healthy = scenario === "healthy";
  const stopped = scenario === "stopped";
  const issue = scenario === "gateway-changed";
  const statusTone = healthy ? "good" : stopped ? "muted" : "attention";
  const heading = busy
    ? "正在检查访问链路"
    : healthy
      ? "国内外分流运行正常"
      : stopped
        ? "分流已暂停"
        : issue
          ? "网关地址需要更新"
          : "等待 VPN 重新连接";
  const subtitle = busy
    ? "正在检查直连组件、网关与访问路径。"
    : healthy
      ? `国内走本地宽带，海外走 ${provider} VPN。`
      : stopped
        ? "启用后，国内与海外访问将按规则分流。"
        : issue
          ? "当前网络已更换，海外访问等待恢复。"
          : `请先在 ${provider} 中连接 VPN，再重新检测。`;

  function chooseProvider(next) {
    if (next !== provider) {
      setProvider(next);
      setScenario("stopped");
      setErrors({});
      setConfirmed(false);
    }
    setMenuOpen(false);
  }
  function runChecks(action = "check") {
    if (busy) return;
    setBusy(true);
    clearTimeout(operationTimer.current);
    operationTimer.current = setTimeout(() => {
      const vpnUnavailable = scenario === "vpn-offline";
      if (!vpnUnavailable && (action === "repair" || action === "enable")) {
        setScenario("healthy");
      }
      setBusy(false);
      setToast(
        vpnUnavailable
          ? "VPN 尚未连接，请先在官方客户端恢复连接（演示）"
          : action === "check"
            ? "演示检测已完成，结果已更新"
            : "演示链路已就绪，未修改本机网络设置",
      );
    }, 950);
  }
  function openSettings(nextStep = 1) {
    setTab("settings");
    setSettingsSection("connection");
    setStep(nextStep);
  }
  function primaryAction() {
    if (issue) {
      setErrors({});
      setDialog("repair");
    } else if (healthy) {
      setScenario("stopped");
      setToast("演示分流已暂停，未修改本机网络设置");
    } else if (stopped) runChecks("enable");
    else openSettings(1);
  }
  function validateForm(gatewayOnly = false) {
    const nextErrors = formErrors(provider, values);
    if (gatewayOnly) delete nextErrors.mixedPort;
    setErrors(nextErrors);
    if (!gatewayOnly && Object.keys(nextErrors).length) {
      setStep(
        Object.keys(nextErrors).some((key) => key !== "mixedPort") ? 1 : 2,
      );
    }
    return !Object.keys(nextErrors).length;
  }
  function downloadProfile() {
    if (!validateForm()) {
      setToast("请修正标出的配置项");
      return;
    }
    try {
      saveFile(
        `Clash-${provider}.local.yaml`,
        createProfile(templates[provider], provider, values),
        "application/yaml;charset=utf-8",
      );
      setToast("配置已生成；请在 Clash Verge 中导入并绑定 DNS 扩展");
    } catch (error) {
      setToast(error.message);
    }
  }
  function exportReport() {
    saveFile(
      "split-helper-diagnostics.json",
      JSON.stringify(
        {
          mode: "interactive-prototype",
          provider,
          scenario,
          scope: "TCP system proxy",
          checks: {
            helper: stopped ? "not-started" : "demo-ready",
            overseas: scenario,
            dns: healthy ? "demo-passed" : "not-verified",
          },
          credentials: "excluded",
          gatewayAddress: "redacted",
        },
        null,
        2,
      ),
      "application/json",
    );
    setToast("已生成脱敏的演示诊断报告");
  }
  const primaryLabel = busy
    ? "正在检测"
    : issue
      ? "更新并重新检测"
      : healthy
        ? "暂停分流"
        : stopped
          ? "启用分流"
          : "查看 VPN 设置";
  const PrimaryIcon = busy
    ? ArrowsClockwise
    : healthy
      ? Pause
      : stopped
        ? Play
        : null;

  return (
    <div className="app-shell">
      <header className="topbar">
        <a
          className="brand"
          href="#overview"
          onClick={(event) => {
            event.preventDefault();
            setTab("overview");
          }}
        >
          <ShareNetwork size={38} weight="fill" aria-hidden="true" />
          <span>分流助手</span>
        </a>
        <nav className="main-nav" aria-label="主导航">
          {[
            ["overview", "概览"],
            ["settings", "设置"],
            ["diagnostics", "诊断"],
          ].map(([id, label]) => (
            <button
              key={id}
              className={tab === id ? "active" : ""}
              aria-current={tab === id ? "page" : undefined}
              onClick={() => setTab(id)}
            >
              {label}
            </button>
          ))}
        </nav>
        <div
          className="provider-picker"
          onBlur={(event) => {
            if (!event.currentTarget.contains(event.relatedTarget))
              setMenuOpen(false);
          }}
          onKeyDown={(event) => {
            if (event.key === "Escape") setMenuOpen(false);
          }}
        >
          <button
            className="provider-button"
            disabled={busy}
            aria-label="选择 VPN 方案"
            aria-expanded={menuOpen}
            aria-controls="provider-menu"
            onClick={() => setMenuOpen(!menuOpen)}
          >
            <ShieldCheck size={29} />
            <span>{provider}</span>
            <CaretDown size={20} className={menuOpen ? "rotate" : ""} />
          </button>
          {menuOpen && (
            <div
              className="provider-menu"
              id="provider-menu"
              role="group"
              aria-label="VPN 方案"
            >
              {["Mullvad", "Windscribe"].map((name) => (
                <button key={name} onClick={() => chooseProvider(name)}>
                  <ShieldCheck size={20} />
                  <span>{name}</span>
                  {provider === name && <Check size={18} />}
                </button>
              ))}
            </div>
          )}
        </div>
      </header>

      {tab === "overview" && (
        <main className="overview-main">
          <section className={`hero ${statusTone}`} aria-live="polite">
            <div className="headline">
              {busy ? (
                <ArrowsClockwise className="spin" />
              ) : healthy ? (
                <CheckCircle />
              ) : stopped ? (
                <Pause />
              ) : (
                <Warning />
              )}
              <h1>{heading}</h1>
            </div>
            <p className="hero-subtitle">{subtitle}</p>
            <div className="hero-actions">
              <button
                className={`button primary ${statusTone}`}
                disabled={busy}
                onClick={primaryAction}
              >
                {PrimaryIcon && (
                  <PrimaryIcon size={24} className={busy ? "spin" : ""} />
                )}
                {primaryLabel}
              </button>
              <button
                className="button outline"
                onClick={() => setDialog("guide")}
              >
                <ArrowSquareOut size={25} />
                {provider} 设置指引
              </button>
            </div>
          </section>
          <RouteDiagram provider={provider} scenario={scenario} busy={busy} />
          <section className="status-summary" aria-label="链路检测摘要">
            <div className={`summary-row ${stopped ? "muted" : "good"}`}>
              <CheckCircle size={33} />
              <strong>{stopped ? "已暂停" : "已确认"}</strong>
              <span className="summary-dot">·</span>
              <span>
                {stopped ? "分流组件等待启用" : "国内直连与内置组件运行正常"}
              </span>
            </div>
            <div className={`summary-row ${statusTone}`}>
              {healthy ? <CheckCircle size={33} /> : <Clock size={33} />}
              <strong>
                {busy
                  ? "检测中"
                  : healthy
                    ? "已确认"
                    : stopped
                      ? "下一步"
                      : "待处理"}
              </strong>
              <span className="summary-dot">·</span>
              <span>
                {busy
                  ? "正在重新检查海外访问路径"
                  : healthy
                    ? "海外出口与 DNS 分流检测通过"
                    : stopped
                      ? "点击启用分流，检查并恢复访问路径"
                      : issue
                        ? "网关地址与当前网络不一致。"
                        : "官方 VPN 尚未连接，海外路径暂不可用。"}
              </span>
            </div>
          </section>
        </main>
      )}

      {tab === "settings" && (
        <main className="settings-main content-main">
          <div className="page-heading">
            <div>
              <span className="eyebrow">连接与偏好</span>
              <h1>把分流设置好，之后交给助手。</h1>
            </div>
            <div className="segmented" aria-label="设置分类">
              <button
                className={settingsSection === "connection" ? "selected" : ""}
                onClick={() => setSettingsSection("connection")}
              >
                连接向导
              </button>
              <button
                className={settingsSection === "startup" ? "selected" : ""}
                onClick={() => setSettingsSection("startup")}
              >
                自动启动
              </button>
            </div>
          </div>
          {settingsSection === "startup" ? (
            <section className="startup-panel">
              <div className="section-intro">
                <Power size={32} />
                <div>
                  <h2>每次登录，都按你的方式准备好。</h2>
                  <p>区分启动助手和启用分流，避免网络尚未就绪时抢先接管。</p>
                </div>
              </div>
              {[
                [
                  "login",
                  "登录 Windows 后启动助手",
                  "在当前用户登录后启动，不需要每次手动打开。",
                ],
                [
                  "enable",
                  "VPN 就绪后自动启用分流",
                  "等待 VPN、Clash 和直连组件就绪，再检查链路。",
                ],
                [
                  "tray",
                  "关闭窗口后在托盘运行",
                  "保持分流，随时从托盘暂停或查看状态。",
                ],
              ].map(([key, label, description]) => (
                <div className="setting-row" key={key}>
                  <div>
                    <h3>{label}</h3>
                    <p>{description}</p>
                  </div>
                  <button
                    className={`switch ${startup[key] ? "on" : ""}`}
                    role="switch"
                    aria-checked={startup[key]}
                    aria-label={label}
                    onClick={() =>
                      setStartup((previous) => ({
                        ...previous,
                        [key]: !previous[key],
                      }))
                    }
                  >
                    <span />
                  </button>
                </div>
              ))}
              <div className="notice">
                <Info size={23} />
                <p>
                  此处为偏好设置演示，刷新后重置。真实 Windows 启动项尚未接入。
                </p>
              </div>
              <button
                className="button primary good compact"
                onClick={() =>
                  setToast("已保存本次演示偏好；未修改 Windows 启动项")
                }
              >
                <Check size={20} />
                保存演示设置
              </button>
            </section>
          ) : (
            <div className="wizard-layout">
              <aside className="step-sidebar" aria-label="设置步骤">
                {steps.map((name, index) => (
                  <button
                    key={name}
                    className={`step-link ${index === step ? "current" : ""} ${index < step ? "complete" : ""}`}
                    aria-current={index === step ? "step" : undefined}
                    onClick={() => {
                      setStep(index);
                      setErrors({});
                    }}
                  >
                    <span className="step-number">
                      {index < step ? <Check size={18} /> : index + 1}
                    </span>
                    <span>
                      {name}
                      <small>
                        {index === step
                          ? "当前步骤"
                          : index < step
                            ? "已浏览"
                            : "待设置"}
                      </small>
                    </span>
                  </button>
                ))}
                <p className="sidebar-note">
                  向导仅演示操作流程。
                  <br />
                  账号登录在官方客户端完成。
                </p>
              </aside>
              <section className="wizard-content">
                <span className="eyebrow">步骤 {step + 1} / 4</span>
                <h2>
                  {
                    [
                      "先确认，所需软件都已准备好。",
                      `让 ${provider} 准备好分流。`,
                      "配置自动生成，无需手写。",
                      "最后，确认实际访问路径。",
                    ][step]
                  }
                </h2>
                {step === 0 && (
                  <>
                    <p className="section-description">
                      正式桌面应用会检测安装路径、运行实例和端口占用。这里先体验完整设置流程。
                    </p>
                    <div className="environment-list">
                      {[
                        [ShieldCheck, provider, "官方 VPN 客户端"],
                        [Stack, "Clash Verge Rev", "规则模式与服务模式"],
                        [
                          RocketLaunch,
                          "内置直连组件",
                          "随安装包提供，由助手自动准备与管理",
                        ],
                      ].map(([Icon, name, detail]) => (
                        <div className="environment-row" key={name}>
                          <Icon size={30} />
                          <div>
                            <strong>{name}</strong>
                            <p>{detail}</p>
                          </div>
                          <span className="badge good">演示已准备</span>
                        </div>
                      ))}
                    </div>
                    <div className="notice">
                      <Info size={22} />
                      <p>
                        仅将内置直连组件排除出 VPN，主界面与 Clash 内核继续留在
                        VPN 内。
                      </p>
                    </div>
                  </>
                )}
                {step === 1 && (
                  <>
                    <p className="section-description">
                      按照官方客户端显示的信息完成设置，再生成对应的 Clash
                      配置。
                    </p>
                    <div className="provider-choice">
                      {["Mullvad", "Windscribe"].map((name) => (
                        <button
                          key={name}
                          className={provider === name ? "chosen" : ""}
                          disabled={busy}
                          onClick={() => chooseProvider(name)}
                        >
                          <ShieldCheck size={21} />
                          {name}
                          {provider === name && <Check size={18} />}
                        </button>
                      ))}
                    </div>
                    <div
                      className={`vpn-settings-grid ${provider === "Mullvad" ? "mullvad-grid" : ""}`}
                    >
                      <ProviderGuide provider={provider} />
                      {provider === "Windscribe" ? (
                        <div className="gateway-form">
                          <h3>网关信息</h3>
                          <p className="small-note">
                            填写网关凭据，无需 VPN
                            账号密码。密码仅保留在当前页面内存中。
                          </p>
                          <div className="field-pair">
                            <Field
                              label="网关 IP"
                              name="ip"
                              values={values}
                              setValues={setValues}
                              errors={errors}
                            />
                            <Field
                              label="网关端口"
                              name="port"
                              values={values}
                              setValues={setValues}
                              errors={errors}
                            />
                          </div>
                          <Field
                            label="网关用户名"
                            name="username"
                            values={values}
                            setValues={setValues}
                            errors={errors}
                            placeholder="客户端显示的网关用户名"
                          />
                          <Field
                            label="网关密码"
                            name="password"
                            type="password"
                            values={values}
                            setValues={setValues}
                            errors={errors}
                            placeholder="客户端显示的网关密码"
                          />
                        </div>
                      ) : (
                        <div className="mullvad-tip">
                          <ListChecks size={35} />
                          <h3>内置组件，自动完成配置</h3>
                          <p>
                            直连组件随安装包提供。正式桌面应用会自动定位组件，通过
                            Mullvad 官方 Windows CLI
                            配置排除项，并在设置后验证。
                          </p>
                          <p className="small-note">
                            当前原型提供引导与配置生成，不执行本机 CLI。
                          </p>
                        </div>
                      )}
                    </div>
                  </>
                )}
                {step === 2 && (
                  <>
                    <p className="section-description">
                      沿用项目已有的分流规则、DNS 策略
                      {provider === "Windscribe" ? "和防回环规则" : ""}
                      ，只填写本机所需信息。
                    </p>
                    <div className="config-layout">
                      <div>
                        <Field
                          label="Clash 混合端口"
                          name="mixedPort"
                          values={values}
                          setValues={setValues}
                          errors={errors}
                          hint="以 Clash Verge 设置页显示的端口为准。"
                        />
                        <div className="config-facts">
                          <span>
                            <CheckCircle size={19} />
                            国内 ChinaDirect
                          </span>
                          <span>
                            <CheckCircle size={19} />
                            海外 {provider}
                          </span>
                          <span>
                            <CheckCircle size={19} />
                            订阅级 DNS 分流
                          </span>
                          {provider === "Windscribe" && (
                            <span>
                              <CheckCircle size={19} />
                              Windscribe 防回环
                            </span>
                          )}
                        </div>
                      </div>
                      <div className="download-area">
                        <FileCode size={38} />
                        <h3>Clash-{provider}.local.yaml</h3>
                        <p>导入 YAML 后，将 DNS 扩展绑定到该订阅。</p>
                        <div className="download-buttons">
                          <button
                            className="button primary good compact"
                            onClick={downloadProfile}
                          >
                            <DownloadSimple size={21} />
                            下载配置
                          </button>
                          <button
                            className="button outline compact"
                            onClick={() => {
                              saveFile(
                                `Profile-DNS-${provider}.js`,
                                dnsScripts[provider],
                              );
                              setToast("DNS 扩展已下载，请仅绑定到对应订阅");
                            }}
                          >
                            下载 DNS 扩展
                          </button>
                        </div>
                        <button
                          className="text-link"
                          onClick={() => {
                            if (validateForm()) setDialog("preview");
                            else {
                              setToast("请修正标出的配置项");
                            }
                          }}
                        >
                          查看脱敏配置预览 <ArrowRight size={17} />
                        </button>
                      </div>
                    </div>
                    <div className="notice attention">
                      <Info size={23} />
                      <p>
                        {provider === "Windscribe"
                          ? "下载的 YAML 包含网关密码，请仅保存在本机。"
                          : "配置在本机生成，不包含 VPN 账号信息。"}{" "}
                        Clash 设置仍需核对：规则模式、服务模式、TUN 与 IPv6
                        关闭。
                      </p>
                    </div>
                    <a
                      className="text-link"
                      href="https://www.clashverge.dev/guide/profile.html"
                      target="_blank"
                      rel="noreferrer"
                    >
                      查看 Clash Verge 导入步骤 <ArrowSquareOut size={17} />
                    </a>
                  </>
                )}
                {step === 3 && (
                  <>
                    <p className="section-description">
                      正式检测需要在系统代理开启后确认国内出口、海外出口、DNS
                      路径{provider === "Windscribe" ? "与网关防回环" : ""}。
                    </p>
                    <div className="verification-list">
                      {[
                        "内置直连组件已就绪，VPN 排除项已确认",
                        "Clash 使用服务模式和规则模式",
                        "TUN、IPv6 已关闭，DNS 扩展已绑定",
                        `${provider} 已连接，海外出口符合预期`,
                      ].map((text) => (
                        <div key={text}>
                          <ListChecks size={24} />
                          <span>{text}</span>
                        </div>
                      ))}
                    </div>
                    <label className="check-label">
                      <input
                        type="checkbox"
                        checked={confirmed}
                        onChange={(event) => setConfirmed(event.target.checked)}
                      />
                      我已了解这些检查项，开始体验启用流程
                    </label>
                    <div className="notice">
                      <Info size={22} />
                      <p>原型使用演示检测结果，不会连接 VPN 或修改系统代理。</p>
                    </div>
                  </>
                )}
                <div className="wizard-footer">
                  <button
                    className="button ghost compact"
                    disabled={step === 0 || busy}
                    onClick={() => setStep(step - 1)}
                  >
                    <ArrowLeft size={20} />
                    上一步
                  </button>
                  <button
                    className="button primary good compact"
                    disabled={busy || (step === 3 && !confirmed)}
                    onClick={() => {
                      if (step === 1 && !validateForm(true)) return;
                      if (step === 3) {
                        setTab("overview");
                        runChecks("enable");
                      } else setStep(step + 1);
                    }}
                  >
                    {step === 3
                      ? "开始演示检测"
                      : step === 1
                        ? "校验并继续"
                        : "下一步"}
                    <ArrowRight size={20} />
                  </button>
                </div>
              </section>
            </div>
          )}
        </main>
      )}

      {tab === "diagnostics" && (
        <main className="content-main diagnostics-main">
          <div className="page-heading">
            <div>
              <span className="eyebrow">诊断与恢复</span>
              <h1>知道哪里断了，也知道怎么恢复。</h1>
            </div>
            <button className="button outline compact" onClick={exportReport}>
              <DownloadSimple size={21} />
              导出演示诊断
            </button>
          </div>
          <div className="diagnostic-toolbar">
            <label htmlFor="scenario">演示情境</label>
            <select
              id="scenario"
              value={scenario}
              disabled={busy}
              onChange={(event) => setScenario(event.target.value)}
            >
              <option
                value="gateway-changed"
                disabled={provider !== "Windscribe"}
              >
                更换网络，网关地址失效
              </option>
              <option value="healthy">分流正常运行</option>
              <option value="vpn-offline">官方 VPN 断开</option>
              <option value="stopped">已暂停分流</option>
            </select>
            <button
              className="button outline compact"
              disabled={busy}
              onClick={() => runChecks()}
            >
              <ArrowsClockwise size={20} className={busy ? "spin" : ""} />
              {busy ? "检测中" : "重新检测"}
            </button>
          </div>
          <section className="diagnostic-list" aria-label="演示检测结果">
            {[
              [
                RocketLaunch,
                "内置直连组件",
                stopped ? "内置组件等待启用" : "内置组件与本地宽带路径正常",
                stopped ? "muted" : "good",
              ],
              [
                ShieldCheck,
                "官方 VPN 与网关",
                issue
                  ? "网关地址与当前网络不一致"
                  : scenario === "vpn-offline"
                    ? "请先连接官方 VPN"
                    : stopped
                      ? "尚未启用分流"
                      : `${provider} 海外路径正常`,
                statusTone,
              ],
              [
                Globe,
                "DNS 分流",
                healthy
                  ? "国内与海外 DNS 路径符合预期"
                  : "待海外路径就绪后继续检测",
                healthy ? "good" : "muted",
              ],
              [
                Stack,
                "Clash 运行配置",
                healthy
                  ? "服务模式、规则模式与 DNS 扩展符合预期"
                  : "需核对服务模式、规则模式及订阅级 DNS 扩展",
                healthy ? "good" : "muted",
              ],
            ].map(([Icon, title, detail, tone]) => (
              <div className="diagnostic-row" key={title}>
                <Icon size={30} />
                <div>
                  <h3>{title}</h3>
                  <p>{detail}</p>
                </div>
                <span className={`badge ${tone}`}>
                  {tone === "good"
                    ? "演示通过"
                    : tone === "attention"
                      ? "需要处理"
                      : "待检查"}
                </span>
              </div>
            ))}
          </section>
          <div className="recovery-panel">
            <div>
              <h2>
                {healthy
                  ? "链路当前正常"
                  : issue
                    ? "先更新网关地址，再重新检测。"
                    : scenario === "vpn-offline"
                      ? "先恢复 VPN，再检查海外路径。"
                      : "准备好后，一次启用。"}
              </h2>
              <p>海外链路异常时保持当前提示，不自动切换为国内直连。</p>
            </div>
            <button
              className="button primary compact good"
              disabled={busy}
              onClick={healthy ? () => openSettings(1) : primaryAction}
            >
              {healthy ? "查看连接设置" : primaryLabel}
              <ArrowRight size={20} />
            </button>
          </div>
        </main>
      )}

      <footer className="app-footer">
        <span>
          系统代理模式 <span className="footer-dot">·</span>{" "}
          仅接管遵循系统代理的应用
        </span>
        <button className="prototype-label" onClick={() => setDialog("about")}>
          <Info size={16} />
          交互原型 · 演示状态
        </button>
      </footer>
      {toast && (
        <div className="toast" role="status">
          <Info size={21} />
          <span>{toast}</span>
          <button
            className="icon-button"
            aria-label="关闭提示"
            onClick={() => setToast("")}
          >
            <X size={18} />
          </button>
        </div>
      )}

      {dialog === "repair" && (
        <Dialog title="更新网关地址" onClose={() => setDialog(null)}>
          <p className="dialog-description">
            在 Windscribe 的 Proxy Gateway 中确认当前地址和端口，再更新到这里。
          </p>
          <form
            onSubmit={(event) => {
              event.preventDefault();
              const all = formErrors("Windscribe", values);
              const endpointErrors = Object.fromEntries(
                Object.entries(all).filter(([key]) =>
                  ["ip", "port"].includes(key),
                ),
              );
              setErrors(endpointErrors);
              if (!Object.keys(endpointErrors).length) {
                setDialog(null);
                runChecks("repair");
              }
            }}
            noValidate
          >
            <div className="field-pair">
              <Field
                label="网关 IP"
                name="ip"
                values={values}
                setValues={setValues}
                errors={errors}
              />
              <Field
                label="网关端口"
                name="port"
                values={values}
                setValues={setValues}
                errors={errors}
              />
            </div>
            <div className="notice">
              <Info size={22} />
              <p>
                这里演示地址修复流程，不进行真实网络检测。生成可用配置时还需填写网关用户名和密码。
              </p>
            </div>
            <div className="dialog-actions">
              <button
                type="button"
                className="button ghost compact"
                onClick={() => setDialog(null)}
              >
                取消
              </button>
              <button
                type="submit"
                className="button primary compact attention"
              >
                <ArrowsClockwise size={20} />
                更新并重新检测
              </button>
            </div>
          </form>
        </Dialog>
      )}
      {dialog === "guide" && (
        <Dialog title={`${provider} 设置指引`} onClose={() => setDialog(null)}>
          <ProviderGuide provider={provider} />
          <div className="dialog-actions">
            <button
              className="button primary good compact"
              onClick={() => {
                setDialog(null);
                openSettings(1);
              }}
            >
              进入完整设置向导
              <ArrowRight size={19} />
            </button>
          </div>
        </Dialog>
      )}
      {dialog === "preview" && (
        <Dialog
          title="配置预览 · 凭据已隐藏"
          wide
          onClose={() => setDialog(null)}
        >
          <p className="dialog-description">
            保留仓库现有分流规则；实际下载文件使用你填写的网关凭据。
          </p>
          <pre className="code-preview">
            {createProfile(templates[provider], provider, values, true)}
          </pre>
          <div className="dialog-actions">
            <button
              className="button primary good compact"
              onClick={downloadProfile}
            >
              <DownloadSimple size={20} />
              下载配置
            </button>
          </div>
        </Dialog>
      )}
      {dialog === "about" && (
        <Dialog title="交互原型说明" onClose={() => setDialog(null)}>
          <p className="dialog-description">
            当前展示第 3
            张设计方案的交互流程。链路状态、启停、检测与自动启动设置均为演示，不会改变你的
            VPN、系统代理或 Windows 启动项。
          </p>
          <p className="dialog-description">
            配置下载实际可用，基于仓库模板生成。Windscribe
            配置需填写本机网关信息，并按向导完成官方客户端与 Clash 设置。
          </p>
          <button
            className="button primary good compact"
            onClick={() => setDialog(null)}
          >
            知道了
          </button>
        </Dialog>
      )}
    </div>
  );
}
