/*
  Veyon-Style Classroom Controller
  - SignalR command hub at /commandhub
  - WebSocket video stream at /ws/stream
*/

const STORAGE_KEY = "masterKey";
const LEGACY_STORAGE_KEY = "classroom_master_key";
const TEACHER_MAC = "TEACHER-0000";
const FILTER_DOMAINS = ["youtube.com", "instagram.com", "tiktok.com", "discord.com"];
const REMOTE_LOCK_KEYS = [
  "Escape",
  "Tab",
  "AltLeft",
  "AltRight",
  "MetaLeft",
  "MetaRight",
  "ControlLeft",
  "ControlRight",
  "ShiftLeft",
  "ShiftRight"
];

const loginModal = document.getElementById("login-modal");
const masterKeyInput = document.getElementById("master-key-input");
const loginSubmit = document.getElementById("login-submit");
const appShell = document.getElementById("app-shell");
const devicesGrid = document.getElementById("devices-grid");
const webFilterPanel = document.getElementById("web-filter-panel");
const webFilterCloseBtn = document.getElementById("web-filter-close-btn");
const filterDeviceName = document.getElementById("filter-device-name");
const filterDeviceMac = document.getElementById("filter-device-mac");
const webFilterToggles = Array.from(document.querySelectorAll(".web-filter-toggle"));
const rcModal = document.getElementById("rc-modal");
const rcCanvas = document.getElementById("rc-canvas");
const rcCloseBtn = document.getElementById("rc-close-btn");
const rcContext = rcCanvas ? rcCanvas.getContext("2d") : null;

let connection = null;
let ws = null;
let devices = {};
let activeFilterMac = null;
let activeRcMac = null;
let lastRcMouseMoveAt = 0;
let remoteFrameSize = rcCanvas
  ? { width: rcCanvas.width, height: rcCanvas.height }
  : { width: 1280, height: 720 };

if (rcCanvas) {
  rcCanvas.tabIndex = 0;
}

function getStoredMasterKey() {
  return localStorage.getItem(STORAGE_KEY) || "";
}

function migrateLegacyMasterKey() {
  const legacyKey = localStorage.getItem(LEGACY_STORAGE_KEY);
  if (!localStorage.getItem(STORAGE_KEY) && legacyKey) {
    localStorage.setItem(STORAGE_KEY, legacyKey);
  }
}

function showLogin() {
  loginModal.classList.remove("hidden");
  appShell.classList.add("hidden");
}

function hideLogin() {
  loginModal.classList.add("hidden");
  appShell.classList.remove("hidden");
}

function clearRemoteCanvas() {
  if (!rcContext || !rcCanvas) {
    return;
  }

  remoteFrameSize = { width: rcCanvas.width, height: rcCanvas.height };
  rcContext.fillStyle = "#000000";
  rcContext.fillRect(0, 0, rcCanvas.width, rcCanvas.height);
}

function createDefaultWebsiteBlockState() {
  return Object.fromEntries(FILTER_DOMAINS.map((domain) => [domain, false]));
}

function parseBlockedWebsitesCsv(blockedWebsites) {
  const state = createDefaultWebsiteBlockState();
  if (typeof blockedWebsites !== "string" || !blockedWebsites.trim()) {
    return state;
  }

  blockedWebsites
    .split(",")
    .map((domain) => domain.trim().toLowerCase())
    .filter(Boolean)
    .forEach((domain) => {
      state[domain] = true;
    });

  return state;
}

function serializeBlockedWebsiteState(blockedWebsiteState) {
  return Object.entries(blockedWebsiteState || {})
    .filter(([, isBlocked]) => Boolean(isBlocked))
    .map(([domain]) => domain)
    .sort((left, right) => left.localeCompare(right))
    .join(",");
}

function normalizeTimerEndTime(value) {
  if (!value) {
    return null;
  }

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

function normalizeTimerRemainingSeconds(value, fallbackTimerEndTime = null) {
  if (value == null || value === "") {
    if (!fallbackTimerEndTime) {
      return null;
    }

    const remainingSeconds = Math.ceil((new Date(fallbackTimerEndTime).getTime() - Date.now()) / 1000);
    return remainingSeconds >= 0 ? remainingSeconds : null;
  }

  const parsed = Number.parseInt(String(value), 10);
  if (Number.isNaN(parsed) || parsed < 0) {
    return null;
  }

  return parsed;
}

function normalizeDevice(rawDevice, previousDevice) {
  const mac = rawDevice?.macAddress || rawDevice?.mac || previousDevice?.mac || "unknown";
  const blockedWebsitesCsv = typeof rawDevice?.blockedWebsites === "string"
    ? rawDevice.blockedWebsites
    : previousDevice?.blockedWebsitesCsv || "";
  const status = rawDevice?.status || previousDevice?.status || "Offline";
  const timerEndTime = normalizeTimerEndTime(rawDevice?.timerEndTime ?? previousDevice?.timerEndTime);
  const timerRemainingSeconds = normalizeTimerRemainingSeconds(
    rawDevice?.timerRemainingSeconds !== undefined ? rawDevice.timerRemainingSeconds : previousDevice?.timerRemainingSeconds,
    timerEndTime
  );

  return {
    mac,
    hostname: rawDevice?.hostname || previousDevice?.hostname || "Unknown",
    ip: rawDevice?.ipAddress || rawDevice?.ip || previousDevice?.ip || "N/A",
    status,
    isOnline: status.toLowerCase() === "online",
    isLocked: Boolean(rawDevice?.isLocked ?? previousDevice?.isLocked),
    isFrozen: Boolean(rawDevice?.isFrozen ?? previousDevice?.isFrozen),
    isAdminMode: Boolean(rawDevice?.isAdminMode ?? previousDevice?.isAdminMode),
    timerEndTime,
    timerRemainingSeconds,
    blockedWebsitesCsv,
    blockedWebsiteState: parseBlockedWebsitesCsv(blockedWebsitesCsv)
  };
}

function getWebsiteBlockState(mac) {
  if (!mac || !devices[mac]) {
    return createDefaultWebsiteBlockState();
  }

  return devices[mac].blockedWebsiteState;
}

function getTimerRemainingSeconds(device) {
  return typeof device?.timerRemainingSeconds === "number" ? device.timerRemainingSeconds : null;
}

function formatRemainingTime(totalSeconds) {
  const safeSeconds = Math.max(0, totalSeconds ?? 0);
  const hours = Math.floor(safeSeconds / 3600);
  const minutes = Math.floor((safeSeconds % 3600) / 60);
  const seconds = safeSeconds % 60;

  if (hours > 0) {
    return `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
  }

  return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
}

function getTimerBadgeConfig(device) {
  const remainingSeconds = getTimerRemainingSeconds(device);
  if (remainingSeconds == null) {
    return { text: "Timer: Off", active: false };
  }

  return {
    text: `Timer: ${formatRemainingTime(remainingSeconds)}`,
    active: true
  };
}

function getTimerParts(device) {
  const totalSeconds = Math.max(0, getTimerRemainingSeconds(device) ?? 0);

  return {
    hours: Math.floor(totalSeconds / 3600),
    minutes: Math.floor((totalSeconds % 3600) / 60),
    seconds: totalSeconds % 60,
    active: getTimerRemainingSeconds(device) != null
  };
}

function readTimerMinutesFromCard(mac) {
  const card = getDeviceCard(mac);
  if (!card) {
    return null;
  }

  const hours = Number.parseInt(card.querySelector(".t-hh")?.value ?? "0", 10) || 0;
  const minutes = Number.parseInt(card.querySelector(".t-mm")?.value ?? "0", 10) || 0;
  const seconds = Number.parseInt(card.querySelector(".t-ss")?.value ?? "0", 10) || 0;
  const totalMinutes = (hours * 60) + minutes + (seconds / 60);

  return totalMinutes > 0 ? totalMinutes : null;
}

function sanitizeTimerFieldValue(rawValue, maxValue) {
  const digitsOnly = rawValue.replace(/\D/g, "").slice(0, 2);
  if (!digitsOnly) {
    return "";
  }

  const numericValue = Math.min(maxValue, Number.parseInt(digitsOnly, 10) || 0);
  return String(numericValue);
}

function setupTimerInputGroup(group) {
  const fields = Array.from(group.querySelectorAll(".t-in"));

  fields.forEach((field, index) => {
    const maxValue = Number.parseInt(field.dataset.maxValue || "59", 10);

    field.addEventListener("keydown", (event) => {
      const isModifier = event.ctrlKey || event.metaKey || event.altKey;
      const navigationKeys = ["Backspace", "Delete", "ArrowLeft", "ArrowRight", "Tab", "Home", "End"];

      if (isModifier || navigationKeys.includes(event.key)) {
        if (event.key === "Backspace" && field.selectionStart === 0 && field.selectionEnd === 0 && index > 0 && !field.value) {
          event.preventDefault();
          fields[index - 1].focus();
        }
        return;
      }

      if (!/^\d$/.test(event.key)) {
        event.preventDefault();
      }
    });

    field.addEventListener("input", () => {
      const normalizedValue = sanitizeTimerFieldValue(field.value, maxValue);
      field.value = normalizedValue;

      if (normalizedValue.length === 2 && index < fields.length - 1) {
        fields[index + 1].focus();
        fields[index + 1].select();
      }
    });

    field.addEventListener("focus", () => {
      field.select();
    });
  });
}

function syncWebFilterPanel() {
  const device = activeFilterMac ? devices[activeFilterMac] : null;
  const blockedWebsiteState = device ? getWebsiteBlockState(activeFilterMac) : createDefaultWebsiteBlockState();

  if (filterDeviceName) {
    filterDeviceName.textContent = device ? device.hostname : "Select a student";
  }

  if (filterDeviceMac) {
    filterDeviceMac.textContent = device
      ? `${activeFilterMac} - ${device.ip}`
      : "Choose a device card to manage website blocking.";
  }

  webFilterToggles.forEach((toggle) => {
    const domain = toggle.dataset.domain;
    toggle.disabled = !device;
    toggle.checked = Boolean(domain && blockedWebsiteState[domain]);
  });
}

function openWebFilterPanel(targetMac) {
  activeFilterMac = targetMac;
  syncWebFilterPanel();
  webFilterPanel?.classList.add("open");
}

function closeWebFilterPanel() {
  activeFilterMac = null;
  syncWebFilterPanel();
  webFilterPanel?.classList.remove("open");
}

function focusRemoteCanvas() {
  if (!rcCanvas) {
    return;
  }

  rcCanvas.focus({ preventScroll: true });
}

async function lockRemoteKeyboard() {
  if (!("keyboard" in navigator) || typeof navigator.keyboard.lock !== "function") {
    return;
  }

  try {
    await navigator.keyboard.lock();
  } catch (error) {
    await navigator.keyboard.lock(REMOTE_LOCK_KEYS);
  }
}

function getContainedRect(containerWidth, containerHeight, contentWidth, contentHeight) {
  const safeContainerWidth = Math.max(1, containerWidth);
  const safeContainerHeight = Math.max(1, containerHeight);
  const safeContentWidth = Math.max(1, contentWidth);
  const safeContentHeight = Math.max(1, contentHeight);
  const containerAspect = safeContainerWidth / safeContainerHeight;
  const contentAspect = safeContentWidth / safeContentHeight;

  if (contentAspect > containerAspect) {
    const width = safeContainerWidth;
    const height = width / contentAspect;
    return {
      left: 0,
      top: (safeContainerHeight - height) / 2,
      width,
      height
    };
  }

  const height = safeContainerHeight;
  const width = height * contentAspect;
  return {
    left: (safeContainerWidth - width) / 2,
    top: 0,
    width,
    height
  };
}

function getRemoteDisplayRect() {
  if (!rcCanvas) {
    return null;
  }

  const bounds = rcCanvas.getBoundingClientRect();
  const containedRect = getContainedRect(
    bounds.width,
    bounds.height,
    remoteFrameSize.width,
    remoteFrameSize.height
  );

  return {
    left: bounds.left + containedRect.left,
    top: bounds.top + containedRect.top,
    width: containedRect.width,
    height: containedRect.height
  };
}

function drawRemoteFrame(imageBitmap) {
  if (!rcContext || !rcCanvas) {
    return;
  }

  remoteFrameSize = {
    width: imageBitmap.width || rcCanvas.width,
    height: imageBitmap.height || rcCanvas.height
  };

  if (rcCanvas.width !== remoteFrameSize.width || rcCanvas.height !== remoteFrameSize.height) {
    rcCanvas.width = remoteFrameSize.width;
    rcCanvas.height = remoteFrameSize.height;
  }

  rcContext.imageSmoothingEnabled = true;
  rcContext.imageSmoothingQuality = "high";
  rcContext.fillStyle = "#000000";
  rcContext.fillRect(0, 0, rcCanvas.width, rcCanvas.height);
  rcContext.drawImage(imageBitmap, 0, 0, rcCanvas.width, rcCanvas.height);
}

function resolveRemoteVirtualKey(event) {
  const code = event.code || "";
  const key = event.key || "";
  const codeMap = {
    AltLeft: 0xA4,
    AltRight: 0xA5,
    ArrowDown: 0x28,
    ArrowLeft: 0x25,
    ArrowRight: 0x27,
    ArrowUp: 0x26,
    Backquote: 0xC0,
    Backslash: 0xDC,
    Backspace: 0x08,
    BracketLeft: 0xDB,
    BracketRight: 0xDD,
    CapsLock: 0x14,
    Comma: 0xBC,
    ContextMenu: 0x5D,
    ControlLeft: 0xA2,
    ControlRight: 0xA3,
    Delete: 0x2E,
    End: 0x23,
    Enter: 0x0D,
    Equal: 0xBB,
    Escape: 0x1B,
    Home: 0x24,
    Insert: 0x2D,
    MetaLeft: 0x5B,
    MetaRight: 0x5C,
    Minus: 0xBD,
    NumLock: 0x90,
    PageDown: 0x22,
    PageUp: 0x21,
    Pause: 0x13,
    Period: 0xBE,
    PrintScreen: 0x2C,
    Quote: 0xDE,
    ScrollLock: 0x91,
    Semicolon: 0xBA,
    ShiftLeft: 0xA0,
    ShiftRight: 0xA1,
    Slash: 0xBF,
    Space: 0x20,
    Tab: 0x09
  };

  if (codeMap[code] !== undefined) {
    return codeMap[code];
  }

  if (code.startsWith("Key") && code.length === 4) {
    return code.charCodeAt(3);
  }

  if (code.startsWith("Digit") && code.length === 6) {
    return code.charCodeAt(5);
  }

  if (code.startsWith("Numpad")) {
    const suffix = code.slice(6);
    const numpadMap = {
      Add: 0x6B,
      Decimal: 0x6E,
      Divide: 0x6F,
      Enter: 0x0D,
      Multiply: 0x6A,
      Subtract: 0x6D
    };

    if (/^[0-9]$/.test(suffix)) {
      return 0x60 + Number(suffix);
    }

    if (numpadMap[suffix] !== undefined) {
      return numpadMap[suffix];
    }
  }

  if (/^F([1-9]|1[0-9]|2[0-4])$/.test(code)) {
    return 0x6F + Number(code.slice(1));
  }

  const keyMap = {
    Alt: 0x12,
    Backspace: 0x08,
    CapsLock: 0x14,
    Control: 0x11,
    Delete: 0x2E,
    End: 0x23,
    Enter: 0x0D,
    Escape: 0x1B,
    Home: 0x24,
    Insert: 0x2D,
    Meta: 0x5B,
    PageDown: 0x22,
    PageUp: 0x21,
    Shift: 0x10,
    Tab: 0x09
  };

  if (keyMap[key] !== undefined) {
    return keyMap[key];
  }

  if (key.length === 1) {
    const upperKey = key.toUpperCase();
    if (/^[A-Z0-9]$/.test(upperKey)) {
      return upperKey.charCodeAt(0);
    }
  }

  if (typeof event.keyCode === "number" && event.keyCode > 0) {
    return event.keyCode;
  }

  return null;
}

async function openRemoteControl(targetMac) {
  activeRcMac = targetMac;
  clearRemoteCanvas();
  rcModal.classList.remove("hidden");
  focusRemoteCanvas();

  try {
    if (typeof rcModal.requestFullscreen === "function" && document.fullscreenElement !== rcModal) {
      await rcModal.requestFullscreen();
    }

    await lockRemoteKeyboard();
    focusRemoteCanvas();
  } catch (error) {
    console.warn("Failed to enable fullscreen keyboard lock:", error);
  }

  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    return;
  }

  try {
    await connection.invoke("SetAdminMode", targetMac, true);
  } catch (error) {
    console.error("SetAdminMode high-trust enable error:", error);
  }

  try {
    await connection.invoke("SetStreamQuality", targetMac, "high");
  } catch (error) {
    console.error("SetStreamQuality high error:", error);
  }
}

async function closeRemoteControl() {
  const targetMac = activeRcMac;

  if ("keyboard" in navigator && typeof navigator.keyboard.unlock === "function") {
    navigator.keyboard.unlock();
  }

  if (document.fullscreenElement) {
    try {
      await document.exitFullscreen();
    } catch (error) {
      console.warn("Failed to exit fullscreen:", error);
    }
  }

  if (targetMac && connection && connection.state === signalR.HubConnectionState.Connected) {
    try {
      await connection.invoke("SetAdminMode", targetMac, false);
    } catch (error) {
      console.error("SetAdminMode disable error:", error);
    }

    try {
      await connection.invoke("SetStreamQuality", targetMac, "low");
    } catch (error) {
      console.error("SetStreamQuality low error:", error);
    }
  }

  activeRcMac = null;
  rcModal.classList.add("hidden");
  clearRemoteCanvas();
}

async function sendRemoteMouseInput(event) {
  if (!activeRcMac || !connection || connection.state !== signalR.HubConnectionState.Connected) {
    return;
  }

  if (event.type === "mousedown") {
    focusRemoteCanvas();
  }

  if (event.type === "mousedown" && document.fullscreenElement) {
    lockRemoteKeyboard().catch((error) => {
      console.log("Keyboard lock failed:", error);
    });
  }

  if (event.type === "mousemove") {
    const now = Date.now();
    if (now - lastRcMouseMoveAt < 50) {
      return;
    }

    lastRcMouseMoveAt = now;
  }

  event.preventDefault();

  const displayRect = getRemoteDisplayRect();
  if (!displayRect) {
    return;
  }

  const localX = event.clientX - displayRect.left;
  const localY = event.clientY - displayRect.top;
  if (localX < 0 || localX > displayRect.width || localY < 0 || localY > displayRect.height) {
    return;
  }

  const xPct = localX / displayRect.width;
  const yPct = localY / displayRect.height;

  try {
    await connection.invoke("SendMouseInput", activeRcMac, event.type, xPct, yPct, event.button);
  } catch (error) {
    console.error("SendMouseInput error:", error);
  }
}

async function sendCommand(targetMac, action) {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    alert("Not connected to server");
    return;
  }

  try {
    const wasSent = await connection.invoke("SendCommand", targetMac, action, null);
    if (!wasSent) {
      alert("Device is offline or unreachable.");
    }
  } catch (error) {
    console.error("SendCommand error:", error);
    alert("Failed to send command");
  }
}

async function toggleLock(mac) {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    alert("Not connected to server");
    return;
  }

  try {
    await connection.invoke("ToggleLock", mac);
  } catch (error) {
    console.error("ToggleLock error:", error);
    alert("Failed to update lock state");
  }
}

async function toggleFreeze(mac) {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    alert("Not connected to server");
    return;
  }

  try {
    await connection.invoke("ToggleFreeze", mac);
  } catch (error) {
    console.error("ToggleFreeze error:", error);
    alert("Failed to update freeze state");
  }
}

async function toggleAdminMode(mac) {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    alert("Not connected to server");
    return;
  }

  try {
    await connection.invoke("ToggleAdminMode", mac);
  } catch (error) {
    console.error("ToggleAdminMode error:", error);
    alert("Failed to update Admin Mode");
  }
}

async function toggleWebsiteBlock(domain, isBlocked) {
  if (!activeFilterMac) {
    throw new Error("No active filter target selected.");
  }

  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    throw new Error("Not connected to server.");
  }

  await connection.invoke("ToggleWebsiteBlock", activeFilterMac, domain, isBlocked);
}

async function toggleTimer(mac) {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    alert("Not connected to server");
    return;
  }

  const device = devices[mac];
  if (device?.timerRemainingSeconds != null) {
    const previousTimerRemainingSeconds = device.timerRemainingSeconds;
    const previousTimerEndTime = device.timerEndTime;
    device.timerRemainingSeconds = null;
    device.timerEndTime = null;
    refreshDeviceCard(mac);

    try {
      await connection.invoke("StopTimer", mac);
    } catch (error) {
      device.timerRemainingSeconds = previousTimerRemainingSeconds;
      device.timerEndTime = previousTimerEndTime;
      refreshDeviceCard(mac);
      console.error("StopTimer error:", error);
      alert("Failed to stop timer");
    }
    return;
  }

  const minutes = readTimerMinutesFromCard(mac);
  if (!minutes || minutes <= 0) {
    alert("Set a timer duration greater than 0.");
    return;
  }

  const totalSeconds = Math.max(1, Math.ceil(minutes * 60));
  const previousState = {
    isLocked: Boolean(device?.isLocked),
    isFrozen: Boolean(device?.isFrozen),
    timerRemainingSeconds: device?.timerRemainingSeconds ?? null,
    timerEndTime: device?.timerEndTime ?? null
  };

  try {
    device.isLocked = false;
    device.isFrozen = false;
    device.timerRemainingSeconds = totalSeconds;
    device.timerEndTime = null;
    refreshDeviceCard(mac);
    await connection.invoke("SetTimer", mac, minutes);
  } catch (error) {
    device.isLocked = previousState.isLocked;
    device.isFrozen = previousState.isFrozen;
    device.timerRemainingSeconds = previousState.timerRemainingSeconds;
    device.timerEndTime = previousState.timerEndTime;
    refreshDeviceCard(mac);
    console.error("SetTimer error:", error);
    alert("Failed to start timer");
  }
}

async function powerOnDevice(mac) {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    alert("Not connected to server");
    return;
  }

  try {
    await connection.invoke("PowerOnDevice", mac);
  } catch (error) {
    console.error("PowerOnDevice error:", error);
    alert("Failed to send Wake-on-LAN packet");
  }
}

function updateDeviceStatus(mac, status) {
  if (!devices[mac]) {
    refreshDevices().catch((error) => {
      console.error("Failed to refresh devices after status update:", error);
    });
    return;
  }

  devices[mac].status = status;
  devices[mac].isOnline = status.toLowerCase() === "online";
  refreshDeviceCard(mac);
}

function updateDeviceState(rawDevice) {
  const mac = rawDevice?.macAddress || rawDevice?.mac;
  if (!mac) {
    return;
  }

  devices[mac] = normalizeDevice(rawDevice, devices[mac]);
  ensureDeviceCard(mac);
  refreshDeviceCard(mac);
  syncWebFilterPanel();
}

function applyDeviceStateUpdate(mac, isLocked, isFrozen, isAdminMode, timerRemainingSeconds) {
  if (!devices[mac]) {
    refreshDevices().catch((error) => {
      console.error("Failed to refresh devices after state update:", error);
    });
    return;
  }

  devices[mac].isLocked = Boolean(isLocked);
  devices[mac].isFrozen = Boolean(isFrozen);
  devices[mac].isAdminMode = Boolean(isAdminMode);
  devices[mac].timerRemainingSeconds = normalizeTimerRemainingSeconds(timerRemainingSeconds);

  if (devices[mac].isLocked || devices[mac].isFrozen || devices[mac].timerRemainingSeconds == null) {
    devices[mac].timerEndTime = null;
  }

  refreshDeviceCard(mac);
  syncWebFilterPanel();
}

function getDeviceVisualState(device) {
  if (!device.isOnline) {
    return {
      badgeText: "Offline",
      badgeClass: "offline",
      overlayText: "OFFLINE",
      overlayClass: "status-grey"
    };
  }

  if (device.isLocked) {
    return {
      badgeText: "Locked",
      badgeClass: "locked",
      overlayText: "LOCKED",
      overlayClass: "status-red"
    };
  }

  if (device.isFrozen) {
    return {
      badgeText: "Frozen",
      badgeClass: "frozen",
      overlayText: "FROZEN",
      overlayClass: "status-yellow"
    };
  }

  if (getTimerRemainingSeconds(device) != null) {
    return {
      badgeText: "Timer",
      badgeClass: "timer",
      overlayText: "ACTIVE",
      overlayClass: "status-blue"
    };
  }

  if (device.isAdminMode) {
    return {
      badgeText: "Admin",
      badgeClass: "admin",
      overlayText: "ADMIN",
      overlayClass: "status-blue"
    };
  }

  return {
    badgeText: "Online",
    badgeClass: "online",
    overlayText: "ONLINE",
    overlayClass: "status-green"
  };
}

function createTimerSegment(unit, label) {
  const segment = document.createElement("div");
  segment.className = "time-field";
  segment.dataset.timerSegment = unit;

  const value = document.createElement("input");
  value.type = "text";
  value.className = `t-in t-${unit === "hours" ? "hh" : unit === "minutes" ? "mm" : "ss"}`;
  value.dataset.timerValue = unit;
  value.dataset.maxValue = unit === "hours" ? "23" : "59";
  value.inputMode = "numeric";
  value.autocomplete = "off";
  value.maxLength = 2;
  value.placeholder = "00";

  const labelText = document.createElement("span");
  labelText.className = "timer-label";
  labelText.textContent = label;

  segment.appendChild(value);
  segment.appendChild(labelText);
  return segment;
}

function createActionButton(mac, id, label, iconClass, onClick) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = `btn-action action-btn ${id}`;
  button.dataset.action = id;
  button.innerHTML = `<i class="${iconClass}"></i><span>${label}</span>`;
  button.addEventListener("click", async (event) => {
    event.stopPropagation();
    await onClick();
  });
  button.dataset.mac = mac;
  return button;
}

function createDeviceCard(device) {
  const card = document.createElement("article");
  card.className = "computer-card device-card";
  card.dataset.mac = device.mac;

  const screenContainer = document.createElement("div");
  screenContainer.className = "screen-preview screen-container";

  const screenFrame = document.createElement("div");
  screenFrame.className = "screen-frame";

  const canvas = document.createElement("canvas");
  canvas.id = `canvas-${device.mac}`;
  canvas.dataset.mac = device.mac;
  canvas.width = 320;
  canvas.height = 180;

  const canvasContext = canvas.getContext("2d");
  if (canvasContext) {
    canvasContext.fillStyle = "#000000";
    canvasContext.fillRect(0, 0, canvas.width, canvas.height);
  }

  screenFrame.appendChild(canvas);

  const statusOverlay = document.createElement("div");
  statusOverlay.className = "status-overlay-badge status-grey";
  statusOverlay.dataset.role = "status-overlay";
  statusOverlay.textContent = "OFFLINE";
  screenFrame.appendChild(statusOverlay);

  const screenOverlay = document.createElement("div");
  screenOverlay.className = "screen-overlay";

  const overlayButton = document.createElement("button");
  overlayButton.type = "button";
  overlayButton.className = "overlay-button view";
  overlayButton.title = "Open Remote Control";
  overlayButton.innerHTML = '<i class="fas fa-eye"></i>';
  overlayButton.addEventListener("click", async (event) => {
    event.stopPropagation();
    await openRemoteControl(device.mac);
  });

  screenOverlay.appendChild(overlayButton);
  screenFrame.appendChild(screenOverlay);

  const powerButton = document.createElement("button");
  powerButton.type = "button";
  powerButton.className = "btn-poweron";
  powerButton.dataset.action = "poweron-device";
  powerButton.innerHTML = '<i class="fas fa-power-off"></i><span>Power On</span>';
  powerButton.addEventListener("click", async (event) => {
    event.stopPropagation();
    await powerOnDevice(device.mac);
  });
  screenFrame.appendChild(powerButton);

  const offlineOverlay = document.createElement("div");
  offlineOverlay.className = "offline-overlay";

  screenContainer.appendChild(screenFrame);
  screenContainer.appendChild(offlineOverlay);

  const cardInfo = document.createElement("div");
  cardInfo.className = "card-info";

  const header = document.createElement("div");
  header.className = "card-header";

  const hostnameElement = document.createElement("div");
  hostnameElement.className = "card-hostname";
  hostnameElement.dataset.role = "hostname";

  const headerMeta = document.createElement("div");
  headerMeta.className = "card-meta";

  const ipElement = document.createElement("div");
  ipElement.className = "card-ip";
  ipElement.dataset.role = "ip";

  const statusBadge = document.createElement("div");
  statusBadge.className = "status-badge";
  statusBadge.dataset.role = "status";

  header.appendChild(hostnameElement);
  headerMeta.appendChild(ipElement);
  headerMeta.appendChild(statusBadge);
  header.appendChild(headerMeta);

  const timerPanel = document.createElement("div");
  timerPanel.className = "session-control timer-panel";
  timerPanel.dataset.role = "timer-panel";

  const idleContainer = document.createElement("div");
  idleContainer.className = "session-idle";
  idleContainer.dataset.role = "timer-idle";

  const timeInputGroup = document.createElement("div");
  timeInputGroup.className = "time-input-group";
  timeInputGroup.appendChild(createTimerSegment("hours", "HRS"));

  const separatorOne = document.createElement("span");
  separatorOne.className = "separator";
  separatorOne.textContent = ":";
  timeInputGroup.appendChild(separatorOne);

  timeInputGroup.appendChild(createTimerSegment("minutes", "MINS"));

  const separatorTwo = document.createElement("span");
  separatorTwo.className = "separator";
  separatorTwo.textContent = ":";
  timeInputGroup.appendChild(separatorTwo);

  timeInputGroup.appendChild(createTimerSegment("seconds", "SECS"));
  setupTimerInputGroup(timeInputGroup);

  const timerButton = document.createElement("button");
  timerButton.type = "button";
  timerButton.className = "btn btn-success btn-sm timer-cta";
  timerButton.dataset.action = "timer";
  timerButton.innerHTML = '<i class="fas fa-play"></i><span>Start</span>';
  timerButton.addEventListener("click", async (event) => {
    event.stopPropagation();
    await toggleTimer(device.mac);
  });

  idleContainer.appendChild(timeInputGroup);
  idleContainer.appendChild(timerButton);

  const activeContainer = document.createElement("div");
  activeContainer.className = "session-active session-hidden";
  activeContainer.dataset.role = "timer-active";

  const activeButton = document.createElement("button");
  activeButton.type = "button";
  activeButton.className = "btn btn-sm btn-timer w-100";
  activeButton.dataset.action = "timer-active";
  activeButton.innerHTML = `
    <span class="timer-countdown" data-role="timer-countdown">00:00:00</span>
    <span class="timer-stop-overlay"><i class="fas fa-stop-circle"></i> STOP</span>
  `;
  activeButton.addEventListener("click", async (event) => {
    event.stopPropagation();
    await toggleTimer(device.mac);
  });
  activeContainer.appendChild(activeButton);

  timerPanel.appendChild(idleContainer);
  timerPanel.appendChild(activeContainer);

  const footer = document.createElement("div");
  footer.className = "action-grid card-footer";
  footer.appendChild(createActionButton(device.mac, "lock", "Lock", "fas fa-lock", () => toggleLock(device.mac)));
  footer.appendChild(createActionButton(device.mac, "freeze", "Freeze", "fas fa-snowflake", () => toggleFreeze(device.mac)));
  footer.appendChild(createActionButton(device.mac, "admin", "Admin", "fas fa-shield-halved", () => toggleAdminMode(device.mac)));
  footer.appendChild(createActionButton(device.mac, "filter", "Filter", "fas fa-globe", () => {
    openWebFilterPanel(device.mac);
    return Promise.resolve();
  }));
  footer.appendChild(createActionButton(device.mac, "reboot", "Reboot", "fas fa-rotate-right", () => sendCommand(device.mac, "reboot")));
  footer.appendChild(createActionButton(device.mac, "poweroff", "Off", "fas fa-power-off", () => sendCommand(device.mac, "poweroff")));

  cardInfo.appendChild(header);
  cardInfo.appendChild(timerPanel);
  cardInfo.appendChild(footer);

  card.appendChild(screenContainer);
  card.appendChild(cardInfo);

  return card;
}

function getDeviceCard(mac) {
  return document.querySelector(`.device-card[data-mac="${mac}"]`);
}

function ensureDeviceCard(mac) {
  if (!devices[mac]) {
    return null;
  }

  let card = getDeviceCard(mac);
  if (!card) {
    card = createDeviceCard(devices[mac]);
    devicesGrid.appendChild(card);
  }

  return card;
}

function refreshDeviceCard(mac) {
  const device = devices[mac];
  const card = getDeviceCard(mac);
  if (!device || !card) {
    return;
  }

  const visualState = getDeviceVisualState(device);

  card.classList.toggle("is-online", device.isOnline);
  card.classList.toggle("state-online", device.isOnline);
  card.classList.toggle("state-offline", !device.isOnline);
  card.classList.toggle("state-locked", device.isLocked);
  card.classList.toggle("state-frozen", device.isFrozen);
  card.classList.toggle("state-admin", device.isAdminMode);

  const hostnameElement = card.querySelector('[data-role="hostname"]');
  if (hostnameElement) {
    hostnameElement.textContent = device.hostname;
  }

  const ipElement = card.querySelector('[data-role="ip"]');
  if (ipElement) {
    ipElement.textContent = device.ip;
  }

  const statusBadge = card.querySelector('[data-role="status"]');
  if (statusBadge) {
    statusBadge.textContent = visualState.badgeText;
    statusBadge.classList.remove("online", "offline", "locked", "frozen", "timer", "admin");
    statusBadge.classList.add(visualState.badgeClass);
  }

  const statusOverlay = card.querySelector('[data-role="status-overlay"]');
  if (statusOverlay) {
    statusOverlay.textContent = visualState.overlayText;
    statusOverlay.className = `status-overlay-badge ${visualState.overlayClass}`;
  }

  const timerPanel = card.querySelector('[data-role="timer-panel"]');
  const timerIdle = card.querySelector('[data-role="timer-idle"]');
  const timerActive = card.querySelector('[data-role="timer-active"]');
  const timerCountdown = card.querySelector('[data-role="timer-countdown"]');
  if (timerPanel) {
    const timerParts = getTimerParts(device);
    timerPanel.classList.toggle("session-hidden", device.isLocked);
    timerPanel.classList.toggle("active", timerParts.active);

    if (timerIdle) {
      timerIdle.classList.toggle("session-hidden", timerParts.active);
    }

    if (timerActive) {
      timerActive.classList.toggle("session-hidden", !timerParts.active);
    }

    if (timerCountdown) {
      timerCountdown.textContent = `${String(timerParts.hours).padStart(2, "0")}:${String(timerParts.minutes).padStart(2, "0")}:${String(timerParts.seconds).padStart(2, "0")}`;
    }
  }

  const lockButton = card.querySelector('[data-action="lock"]');
  if (lockButton) {
    lockButton.querySelector("span").textContent = device.isLocked ? "Unlock" : "Lock";
    lockButton.classList.toggle("toggle-active", device.isLocked);
  }

  const freezeButton = card.querySelector('[data-action="freeze"]');
  if (freezeButton) {
    freezeButton.querySelector("span").textContent = device.isFrozen ? "Unfreeze" : "Freeze";
    freezeButton.classList.toggle("toggle-active", device.isFrozen);
  }

  const adminButton = card.querySelector('[data-action="admin"]');
  if (adminButton) {
    adminButton.querySelector("span").textContent = device.isAdminMode ? "Admin On" : "Admin";
    adminButton.classList.toggle("toggle-active", device.isAdminMode);
  }

  const timerButton = card.querySelector('[data-action="timer"]');
  if (timerButton) {
    timerButton.classList.toggle("toggle-active", getTimerRemainingSeconds(device) != null);
    const timerLabel = timerButton.querySelector("span");
    if (timerLabel) {
      timerLabel.textContent = getTimerRemainingSeconds(device) != null ? "Clear" : "Start";
    }
  }

  const isOffline = !device.isOnline;
  card.querySelectorAll(".btn-action, .overlay-button, .timer-cta, .btn-timer, .t-in").forEach((element) => {
    element.disabled = isOffline;
  });

  const powerButton = card.querySelector('[data-action="poweron-device"]');
  if (powerButton) {
    powerButton.disabled = !isOffline;
  }
}

function renderDeviceCards(deviceList) {
  const nextDevices = {};
  if (Array.isArray(deviceList)) {
    deviceList.forEach((rawDevice) => {
      const mac = rawDevice?.macAddress || rawDevice?.mac;
      if (!mac) {
        return;
      }

      nextDevices[mac] = normalizeDevice(rawDevice, devices[mac]);
    });
  }

  devices = nextDevices;
  devicesGrid.innerHTML = "";

  Object.values(devices).forEach((device) => {
    const card = createDeviceCard(device);
    devicesGrid.appendChild(card);
    refreshDeviceCard(device.mac);
  });

  if (activeFilterMac && !devices[activeFilterMac]) {
    closeWebFilterPanel();
  } else {
    syncWebFilterPanel();
  }
}

function tickTimerBadges() {
  Object.keys(devices).forEach((mac) => {
    const device = devices[mac];
    if (device?.timerRemainingSeconds != null && device.timerRemainingSeconds > 0) {
      device.timerRemainingSeconds -= 1;
    }

    refreshDeviceCard(mac);
  });
}

async function refreshDevices() {
  const response = await fetch("/api/devices");
  const deviceList = await response.json();
  renderDeviceCards(deviceList);
}

async function beginSignalR() {
  const masterKey = getStoredMasterKey();

  connection = new signalR.HubConnectionBuilder()
    .withUrl(`/commandhub?key=${encodeURIComponent(masterKey)}`)
    .withAutomaticReconnect()
    .build();

  connection.on("UpdateDeviceList", (deviceList) => {
    renderDeviceCards(deviceList);
  });

  connection.on("DeviceStatusChanged", (mac, status) => {
    updateDeviceStatus(mac, status);
  });

  connection.on("DeviceStateChanged", (device) => {
    updateDeviceState(device);
  });

  connection.on("UpdateDeviceState", (mac, isLocked, isFrozen, isAdminMode, timerRemainingSeconds) => {
    applyDeviceStateUpdate(mac, isLocked, isFrozen, isAdminMode, timerRemainingSeconds);
  });

  connection.onreconnecting((error) => {
    console.log("SignalR reconnecting", error);
  });

  connection.onreconnected(async () => {
    console.log("SignalR reconnected");
    try {
      await refreshDevices();
    } catch (error) {
      console.error("Failed to refresh devices after reconnect:", error);
    }
  });

  connection.onclose((error) => {
    console.log("SignalR disconnected", error);
  });

  try {
    await connection.start();
    console.log("SignalR connected successfully");
  } catch (error) {
    console.error("SignalR connection failed:", error);
    setTimeout(beginSignalR, 5000);
  }
}

function beginVideoSocket() {
  const protocol = window.location.protocol === "https:" ? "wss" : "ws";
  const url = `${protocol}://${window.location.host}/ws/stream?role=teacher&mac=${encodeURIComponent(TEACHER_MAC)}&key=${encodeURIComponent(getStoredMasterKey())}`;

  ws = new WebSocket(url);
  ws.binaryType = "arraybuffer";

  ws.onopen = () => {
    console.log("WebSocket video stream connected");
  };

  ws.onerror = (error) => {
    console.error("WebSocket error:", error);
  };

  ws.onclose = () => {
    console.log("WebSocket closed, will retry in 3 seconds...");
    setTimeout(beginVideoSocket, 3000);
  };

  ws.onmessage = async (event) => {
    if (!(event.data instanceof ArrayBuffer)) {
      return;
    }

    const raw = new Uint8Array(event.data);
    if (raw.length < 12) {
      return;
    }

    const macBytes = raw.slice(0, 12);
    const macNoDash = new TextDecoder("ascii").decode(macBytes).trim();
    const jpegData = raw.slice(12);
    const normalizedMac = macNoDash.toUpperCase();
    const macDashed = normalizedMac.match(/.{1,2}/g)?.join("-") ?? normalizedMac;
    const shouldRenderRemote = macDashed === activeRcMac;

    if (!devices[macDashed]) {
      return;
    }

    if (!devices[macDashed].isOnline) {
      devices[macDashed].status = "Online";
      devices[macDashed].isOnline = true;
      refreshDeviceCard(macDashed);
    }

    const canvas = document.getElementById(`canvas-${macDashed}`);
    if (!canvas && !shouldRenderRemote) {
      return;
    }

    try {
      const blob = new Blob([jpegData], { type: "image/jpeg" });
      const imageBitmap = await createImageBitmap(blob);

      if (canvas instanceof HTMLCanvasElement) {
        const context = canvas.getContext("2d");
        if (context) {
          context.drawImage(imageBitmap, 0, 0, canvas.width, canvas.height);
        }
      }

      if (shouldRenderRemote && rcContext) {
        drawRemoteFrame(imageBitmap);
      }

      imageBitmap.close();
    } catch (error) {
      console.warn("Video frame render failed for MAC:", macDashed, error);
    }
  };
}

async function startApp() {
  await beginSignalR();

  try {
    await refreshDevices();
  } catch (error) {
    console.error("Failed to fetch initial devices:", error);
  }

  beginVideoSocket();
}

function checkAuth() {
  migrateLegacyMasterKey();
  const savedKey = getStoredMasterKey();
  if (savedKey) {
    hideLogin();
    startApp();
    return;
  }

  showLogin();
}

loginSubmit.addEventListener("click", () => {
  const key = masterKeyInput.value.trim();
  if (!key) {
    alert("Master Key is required.");
    return;
  }

  localStorage.setItem(STORAGE_KEY, key);
  localStorage.setItem(LEGACY_STORAGE_KEY, key);
  hideLogin();
  startApp();
});

rcCloseBtn?.addEventListener("click", () => {
  closeRemoteControl().catch((error) => {
    console.error("Close remote control error:", error);
  });
});

webFilterCloseBtn?.addEventListener("click", closeWebFilterPanel);

webFilterToggles.forEach((toggle) => {
  toggle.addEventListener("change", async (event) => {
    const checkbox = event.currentTarget;
    const domain = checkbox?.dataset.domain;

    if (!(checkbox instanceof HTMLInputElement) || !domain) {
      return;
    }

    if (!activeFilterMac || !devices[activeFilterMac]) {
      checkbox.checked = false;
      return;
    }

    const previousState = { ...getWebsiteBlockState(activeFilterMac) };
    const nextState = {
      ...previousState,
      [domain]: checkbox.checked
    };

    devices[activeFilterMac].blockedWebsiteState = nextState;
    devices[activeFilterMac].blockedWebsitesCsv = serializeBlockedWebsiteState(nextState);
    syncWebFilterPanel();

    try {
      await toggleWebsiteBlock(domain, checkbox.checked);
    } catch (error) {
      devices[activeFilterMac].blockedWebsiteState = previousState;
      devices[activeFilterMac].blockedWebsitesCsv = serializeBlockedWebsiteState(previousState);
      checkbox.checked = Boolean(previousState[domain]);
      syncWebFilterPanel();
      console.error("ToggleWebsiteBlock error:", error);
      alert("Failed to update website filter");
    }
  });
});

if (rcCanvas) {
  ["mousemove", "mousedown", "mouseup"].forEach((eventName) => {
    rcCanvas.addEventListener(eventName, sendRemoteMouseInput);
  });

  rcCanvas.addEventListener("contextmenu", (event) => event.preventDefault());
}

["keydown", "keyup"].forEach((eventName) => {
  document.addEventListener(eventName, (event) => {
    if (!activeRcMac || !connection || connection.state !== signalR.HubConnectionState.Connected) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();

    const keyCode = resolveRemoteVirtualKey(event);
    if (keyCode == null) {
      console.warn("Unable to map keyboard event for remote control:", event.type, event.code, event.key);
      return;
    }

    connection.invoke("SendKeyboardInput", activeRcMac, event.type, keyCode).catch((error) => {
      console.error("SendKeyboardInput error:", error);
    });
  }, true);
});

window.setInterval(tickTimerBadges, 1000);

clearRemoteCanvas();
syncWebFilterPanel();
checkAuth();
