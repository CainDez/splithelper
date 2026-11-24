import { parse, stringify } from "yaml";

export function formErrors(provider, values) {
  const errors = {};
  const portIsValid = (value) =>
    /^\d+$/.test(String(value)) && Number(value) >= 1 && Number(value) <= 65535;
  if (!portIsValid(values.mixedPort))
    errors.mixedPort = "请填写 1–65535 之间的 Clash 混合端口";
  if (provider === "Windscribe") {
    const parts = values.ip.trim().split(".");
    if (
      parts.length !== 4 ||
      parts.some(
        (part) =>
          !/^\d{1,3}$/.test(part) ||
          Number(part) > 255 ||
          String(Number(part)) !== part,
      )
    ) {
      errors.ip = "请填写网关界面显示的 IPv4 地址";
    } else if (
      /^(192\.0\.2\.|198\.51\.100\.|203\.0\.113\.)/.test(values.ip.trim())
    ) {
      errors.ip = "这是文档示例地址，请填写本机网关的实际地址";
    }
    if (!portIsValid(values.port))
      errors.port = "请填写 1–65535 之间的网关端口";
    if (!values.username.trim()) errors.username = "请填写网关用户名";
    if (!values.password) errors.password = "请填写网关密码";
  }
  return errors;
}

export function createProfile(template, provider, values, redact = false) {
  if (!["Mullvad", "Windscribe"].includes(provider))
    throw new Error("不支持的 VPN 方案");
  const errors = formErrors(provider, values);
  if (Object.keys(errors).length) throw new Error(Object.values(errors)[0]);
  const config = parse(template);
  config["mixed-port"] = Number(values.mixedPort);
  if (provider === "Windscribe") {
    const gateway = config.proxies.find((proxy) => proxy.name === "Official");
    gateway.server = values.ip.trim();
    gateway.port = Number(values.port);
    gateway.username = redact ? "[已隐藏]" : values.username;
    gateway.password = redact ? "[已隐藏]" : values.password;
  }
  return (
    "# 由分流助手生成。含密配置请仅保存在本机。\n" +
    stringify(config, { lineWidth: 0 })
  );
}
