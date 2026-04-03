/*
  Veyon-Style Classroom Controller
  - SignalR command hub at /commandhub
  - WebSocket video stream at /ws/stream
*/

const STORAGE_KEY = "masterKey";
const LEGACY_STORAGE_KEY = "classroom_master_key";
const TEACHER_MAC = "TEACHER-0000";

const loginModal = document.getElementById("login-modal");
const masterKeyInput = document.getElementById("master-key-input");
const loginSubmit = document.getElementById("login-submit");
const appShell = document.getElementById("app-shell");
const devicesGrid = document.getElementById("devices-grid");
const rcModal = document.getElementById("rc-modal");
const rcCanvas = document.getElementById("rc-canvas");
const rcCloseBtn = document.getElementById("rc-close-btn");
const rcContext = rcCanvas ? rcCanvas.getContext("2d") : null;

let connection;
let ws;
let devices = {};
let activeRcMac = null;
let lastRcMouseMoveAt = 0;

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

  rcContext.fillStyle = "#000000";
  rcContext.fillRect(0, 0, rcCanvas.width, rcCanvas.height);
}

function openRemoteControl(targetMac) {
  activeRcMac = targetMac;
  clearRemoteCanvas();
  rcModal.classList.remove("hidden");
}

function closeRemoteControl() {
  activeRcMac = null;
  rcModal.classList.add("hidden");
  clearRemoteCanvas();
}

async function sendRemoteMouseInput(event) {
  if (!activeRcMac || !connection || connection.state !== signalR.HubConnectionState.Connected) {
    return;
  }

  if (event.type === "mousemove") {
    const now = Date.now();
    if (now - lastRcMouseMoveAt < 50) {
      return;
    }

    lastRcMouseMoveAt = now;
  }

  event.preventDefault();

  const xPct = event.offsetX / rcCanvas.clientWidth;
  const yPct = event.offsetY / rcCanvas.clientHeight;

  try {
    await connection.invoke("SendMouseInput", activeRcMac, event.type, xPct, yPct);
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
      { id: "lock", label: "Lock", action: "lock" },
      { id: "unlock", label: "Unlock", action: "unlock" },
      { id: "reboot", label: "Reboot", action: "reboot" },
      { id: "poweroff", label: "Power Off", action: "poweroff" }
    ];

    actions.forEach(({ id, label, action }) => {
      const btn = document.createElement("button");
      btn.className = `action-btn ${id}`;
      btn.textContent = label;
      btn.onclick = (buttonEvent) => {
        buttonEvent.stopPropagation();

        if (action === "control") {
          openRemoteControl(mac);
          return;
        }

        sendCommand(mac, action);
      };

      footer.appendChild(btn);
    });

    card.appendChild(header);
    card.appendChild(screenContainer);
    card.appendChild(footer);
    devicesGrid.appendChild(card);
  }
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
        rcContext.drawImage(imageBitmap, 0, 0, rcCanvas.width, rcCanvas.height);
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

rcCloseBtn.addEventListener("click", closeRemoteControl);
["mousemove", "mousedown", "mouseup"].forEach((eventName) => {
  rcCanvas.addEventListener(eventName, sendRemoteMouseInput);
});
rcCanvas.addEventListener("contextmenu", (event) => event.preventDefault());

clearRemoteCanvas();
checkAuth();
