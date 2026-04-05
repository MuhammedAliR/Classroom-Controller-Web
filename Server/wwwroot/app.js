/*
  Veyon-Style Classroom Controller
  - SignalR command hub at /commandhub
  - WebSocket video stream at /ws/stream
*/

const STORAGE_KEY = "masterKey";
const LEGACY_STORAGE_KEY = "classroom_master_key";
const TEACHER_MAC = "TEACHER-0000";
const FILTER_DOMAINS = ["youtube.com", "instagram.com", "tiktok.com", "discord.com"];

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

let connection;
let ws;
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

function cloneWebsiteBlockState(existingState) {
  return {
    ...createDefaultWebsiteBlockState(),
    ...(existingState ?? {})
  };
}

function getWebsiteBlockState(mac) {
  if (!mac || !devices[mac]) {
    return createDefaultWebsiteBlockState();
  }

  if (!devices[mac].blockedWebsites) {
    devices[mac].blockedWebsites = createDefaultWebsiteBlockState();
  }

  return devices[mac].blockedWebsites;
}

function syncWebFilterPanel() {
  const device = activeFilterMac ? devices[activeFilterMac] : null;
  const blockedWebsites = device ? getWebsiteBlockState(activeFilterMac) : createDefaultWebsiteBlockState();

  if (filterDeviceName) {
    filterDeviceName.textContent = device ? device.hostname : "Select a student";
  }

  if (filterDeviceMac) {
    filterDeviceMac.textContent = device
      ? `${activeFilterMac} • ${device.ip}`
      : "Choose a device card to manage website blocking.";
  }

  webFilterToggles.forEach((toggle) => {
    const domain = toggle.dataset.domain;
    toggle.disabled = !device;
    toggle.checked = Boolean(domain && blockedWebsites[domain]);
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

function setAdminModeUi(mac, isAdminMode) {
  if (devices[mac]) {
    devices[mac].isAdminMode = isAdminMode;
  }

  const button = document.querySelector(`button[data-admin-toggle-for="${mac}"]`);
  if (!button) {
    return;
  }

  button.classList.toggle("is-admin-on", isAdminMode);
  button.textContent = isAdminMode ? "Admin On" : "Admin Off";
  button.title = isAdminMode ? "Admin Mode is enabled. Restrictions are lifted." : "Student Mode is active. Restrictions are enforced.";
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
  } catch (err) {
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

    if (/^[0-9]$/.test(suffix)) {
      return 0x60 + Number(suffix);
    }

    const numpadMap = {
      Add: 0x6B,
      Decimal: 0x6E,
      Divide: 0x6F,
      Enter: 0x0D,
      Multiply: 0x6A,
      Subtract: 0x6D
    };

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
  } catch (err) {
    console.warn("Failed to enable fullscreen keyboard lock:", err);
  }

  if (connection && connection.state === signalR.HubConnectionState.Connected) {
    await toggleAdminMode(targetMac, true);

    try {
      await connection.invoke("SetStreamQuality", targetMac, "high");
    } catch (err) {
      console.error("SetStreamQuality high error:", err);
    }
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
    } catch (err) {
      console.warn("Failed to exit fullscreen:", err);
    }
  }

  if (targetMac && connection && connection.state === signalR.HubConnectionState.Connected) {
    await toggleAdminMode(targetMac, false);

    try {
      await connection.invoke("SetStreamQuality", targetMac, "low");
    } catch (err) {
      console.error("SetStreamQuality low error:", err);
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
    lockRemoteKeyboard().catch((err) => {
      console.log("Keyboard lock failed:", err);
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
  } catch (err) {
    console.error("SendMouseInput error:", err);
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
  } catch (err) {
    console.error("SendCommand error:", err);
    alert("Failed to send command");
  }
}

async function toggleAdminMode(mac, isAdmin) {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    alert("Not connected to server");
    return;
  }

  try {
    await connection.invoke("ToggleAdminMode", mac, isAdmin);
    setAdminModeUi(mac, isAdmin);
  } catch (err) {
    console.error("ToggleAdminMode error:", err);
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

function updateDeviceStatus(mac, status) {
  const isOnline = status.toLowerCase() === "online";

  if (devices[mac]) {
    devices[mac].status = status;
    devices[mac].isOnline = isOnline;
  }

  const card = document.querySelector(`[data-mac="${mac}"]`);
  if (!card) {
    return;
  }

  if (isOnline) {
    card.classList.add("is-online");
  } else {
    card.classList.remove("is-online");
  }

  const badge = card.querySelector(".status-badge");
  if (badge) {
    badge.dataset.status = status;
    badge.textContent = status;
    badge.classList.remove("online", "offline");
    badge.classList.add(isOnline ? "online" : "offline");
  }
}

function renderDeviceCards(deviceList) {
  devicesGrid.innerHTML = "";
  const previousDevices = devices;
  devices = {};

  if (!Array.isArray(deviceList)) {
    console.warn("UpdateDeviceList payload was not an array.", deviceList);
    return;
  }

  for (const device of deviceList) {
    const mac = device.macAddress || device.mac || "unknown";
    const hostname = device.hostname || "Unknown";
    const status = device.status || "Offline";
    const ip = device.ipAddress || device.ip || "N/A";

    devices[mac] = {
      mac,
      hostname,
      status,
      ip,
      isAdminMode: previousDevices[mac]?.isAdminMode ?? false,
      blockedWebsites: cloneWebsiteBlockState(previousDevices[mac]?.blockedWebsites),
      isOnline: status.toLowerCase() === "online"
    };

    const card = document.createElement("article");
    card.className = "device-card";
    if (devices[mac].isOnline) {
      card.classList.add("is-online");
    }
    card.dataset.mac = mac;

    const header = document.createElement("div");
    header.className = "card-header";

    const hostnameElement = document.createElement("div");
    hostnameElement.className = "card-hostname";
    hostnameElement.textContent = hostname;

    const statusBadge = document.createElement("div");
    statusBadge.className = `status-badge ${devices[mac].isOnline ? "online" : "offline"}`;
    statusBadge.dataset.status = status;
    statusBadge.textContent = status;

    header.appendChild(hostnameElement);
    header.appendChild(statusBadge);

    const screenContainer = document.createElement("div");
    screenContainer.className = "screen-container";

    const screenFrame = document.createElement("div");
    screenFrame.className = "screen-frame";

    const canvas = document.createElement("canvas");
    canvas.id = `canvas-${mac}`;
    canvas.setAttribute("data-mac", mac);
    canvas.width = 320;
    canvas.height = 180;

    const ctx = canvas.getContext("2d");
    if (ctx) {
      ctx.fillStyle = "#000000";
      ctx.fillRect(0, 0, canvas.width, canvas.height);
    }

    screenFrame.appendChild(canvas);

    const offlineOverlay = document.createElement("div");
    offlineOverlay.className = "offline-overlay";
    offlineOverlay.textContent = "OFFLINE";

    screenContainer.appendChild(screenFrame);
    screenContainer.appendChild(offlineOverlay);

    const footer = document.createElement("div");
    footer.className = "card-footer";

    const actions = [
      { id: "control", label: "Control", action: "control" },
      { id: "filter", label: "Filter", action: "filter" },
      { id: "lock", label: "Lock", action: "lock" },
      { id: "unlock", label: "Unlock", action: "unlock" },
      { id: "reboot", label: "Reboot", action: "reboot" },
      { id: "poweroff", label: "Power Off", action: "poweroff" }
    ];

    actions.forEach(({ id, label, action }) => {
      const btn = document.createElement("button");
      btn.className = `action-btn ${id}`;
      btn.textContent = label;
      btn.onclick = async (buttonEvent) => {
        buttonEvent.stopPropagation();

        if (action === "control") {
          await openRemoteControl(mac);
          return;
        }

        if (action === "filter") {
          openWebFilterPanel(mac);
          return;
        }

        sendCommand(mac, action);
      };

      footer.appendChild(btn);
    });

    const adminBtn = document.createElement("button");
    adminBtn.className = "action-btn admin";
    adminBtn.dataset.adminToggleFor = mac;
    adminBtn.onclick = async (buttonEvent) => {
      buttonEvent.stopPropagation();
      await toggleAdminMode(mac, !devices[mac].isAdminMode);
    };
    footer.appendChild(adminBtn);
    setAdminModeUi(mac, devices[mac].isAdminMode);

    card.appendChild(header);
    card.appendChild(screenContainer);
    card.appendChild(footer);
    devicesGrid.appendChild(card);
  }

  syncWebFilterPanel();
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

  connection.onreconnecting((error) => {
    console.log("SignalR reconnecting", error);
  });

  connection.onreconnected(() => {
    console.log("SignalR reconnected");
  });

  connection.onclose((error) => {
    console.log("SignalR disconnected", error);
  });

  try {
    await connection.start();
    console.log("SignalR connected successfully");
  } catch (err) {
    console.error("SignalR connection failed:", err);
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

  ws.onerror = (err) => {
    console.error("WebSocket error:", err);
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

    if (!devices[macDashed] || !devices[macDashed].isOnline) {
      return;
    }

    let canvas = document.querySelector(`canvas[data-mac="${macDashed}"]`);
    if (!canvas) {
      canvas = document.querySelector(`#canvas-${macDashed}`);
    }

    if (!canvas && !shouldRenderRemote) {
      return;
    }

    try {
      const blob = new Blob([jpegData], { type: "image/jpeg" });
      const imageBitmap = await createImageBitmap(blob);

      if (canvas) {
        const ctx = canvas.getContext("2d");
        if (ctx) {
          ctx.drawImage(imageBitmap, 0, 0, canvas.width, canvas.height);
        }
      }

      if (shouldRenderRemote && rcContext) {
        drawRemoteFrame(imageBitmap);
      }

      imageBitmap.close();
    } catch (err) {
      console.warn("Video frame render failed for MAC:", macDashed, err);
    }
  };
}

async function startApp() {
  await beginSignalR();

  try {
    const response = await fetch("/api/devices");
    const deviceList = await response.json();
    if (Object.keys(devices).length === 0) {
      renderDeviceCards(deviceList);
    }
  } catch (err) {
    console.error("Failed to fetch initial devices:", err);
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

rcCloseBtn.addEventListener("click", () => {
  closeRemoteControl().catch((err) => {
    console.error("Close remote control error:", err);
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

    if (!activeFilterMac) {
      checkbox.checked = false;
      return;
    }

    const blockedWebsites = getWebsiteBlockState(activeFilterMac);
    const previousValue = Boolean(blockedWebsites[domain]);
    const isBlocked = checkbox.checked;
    blockedWebsites[domain] = isBlocked;

    try {
      await toggleWebsiteBlock(domain, isBlocked);
      syncWebFilterPanel();
    } catch (err) {
      blockedWebsites[domain] = previousValue;
      checkbox.checked = previousValue;
      console.error("ToggleWebsiteBlock error:", err);
      alert("Failed to update website filter");
    }
  });
});
["mousemove", "mousedown", "mouseup"].forEach((eventName) => {
  rcCanvas.addEventListener(eventName, sendRemoteMouseInput);
});
rcCanvas.addEventListener("contextmenu", (event) => event.preventDefault());
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

    connection.invoke("SendKeyboardInput", activeRcMac, event.type, keyCode).catch((err) => {
      console.error("SendKeyboardInput error:", err);
    });
  }, true);
});

clearRemoteCanvas();
syncWebFilterPanel();
checkAuth();
