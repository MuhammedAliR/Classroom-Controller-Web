/*
  Veyon-Style Classroom Controller
  - Modern dark UI matching Veyon classroom management
  - SignalR command hub at /commandhub
  - WebSocket video stream at /ws/stream
*/

const STORAGE_KEY = "classroom_master_key";
const TEACHER_MAC = "TEACHER-0000";

// DOM Elements
const loginModal = document.getElementById("login-modal");
const masterKeyInput = document.getElementById("master-key-input");
const loginSubmit = document.getElementById("login-submit");
const appShell = document.getElementById("app-shell");
const devicesGrid = document.getElementById("devices-grid");

console.log("App.js loaded. devicesGrid element:", devicesGrid);

// Toolbar Buttons
const btnSelectAll = document.getElementById("btn-select-all");
const btnLockSelected = document.getElementById("btn-lock-selected");
const btnUnlockSelected = document.getElementById("btn-unlock-selected");
const btnGlobalMessage = document.getElementById("btn-global-message");

// State
let connection;
let ws;
let selectedDevices = new Set();
let devices = {}; // Map of MAC -> device info

// ============ Authentication ============
function checkAuth() {
  const savedKey = localStorage.getItem(STORAGE_KEY);
  if (savedKey) {
    hideLogin();
    startApp();
    return;
  }
  showLogin();
}

function showLogin() {
  loginModal.classList.remove("hidden");
  appShell.classList.add("hidden");
}

function hideLogin() {
  loginModal.classList.add("hidden");
  appShell.classList.remove("hidden");
}

loginSubmit.addEventListener("click", () => {
  const key = masterKeyInput.value.trim();
  if (!key) {
    alert("Master Key is required.");
    return;
  }
  localStorage.setItem(STORAGE_KEY, key);
  hideLogin();
  startApp();
});

// ============ Device Card Rendering ============
function renderDeviceCards(deviceList) {
  console.log("renderDeviceCards called with:", deviceList);
  
  devicesGrid.innerHTML = "";
  selectedDevices.clear();
  devices = {};

  if (!Array.isArray(deviceList)) {
    console.warn("deviceList is not an array:", deviceList);
    return;
  }

  console.log("Rendering", deviceList.length, "devices");

  for (const device of deviceList) {
    const mac = device.macAddress || device.mac || "unknown";
    const hostname = device.hostname || "Unknown";
    const status = device.status || "Offline";
    const ip = device.ipAddress || device.ip || "N/A";

    console.log("Creating card for:", mac, hostname, status);

    // Store device info
    devices[mac] = {
      mac,
      hostname,
      status,
      ip,
      isOnline: status.toLowerCase() === "online"
    };

    // Create card element
    const card = document.createElement("article");
    card.className = "device-card";
    if (devices[mac].isOnline) {
      card.classList.add("is-online");
    }
    card.dataset.mac = mac;

    // Header
    const header = document.createElement("div");
    header.className = "card-header";
    
    const hostname_el = document.createElement("div");
    hostname_el.className = "card-hostname";
    hostname_el.textContent = hostname;

    const statusBadge = document.createElement("div");
    statusBadge.className = `status-badge ${devices[mac].isOnline ? "online" : "offline"}`;
    statusBadge.dataset.status = status;
    statusBadge.textContent = status;

    header.appendChild(hostname_el);
    header.appendChild(statusBadge);

    // Screen Container (16:9 aspect ratio)
    const screenContainer = document.createElement("div");
    screenContainer.className = "screen-container";

    const screenFrame = document.createElement("div");
    screenFrame.className = "screen-frame";

    const canvas = document.createElement("canvas");
    canvas.id = `canvas-${mac}`;
    canvas.width = 320;
    canvas.height = 180;

    // Fill canvas with black
    const ctx = canvas.getContext("2d");
    if (ctx) {
      ctx.fillStyle = "#000000";
      ctx.fillRect(0, 0, canvas.width, canvas.height);
    }

    screenFrame.appendChild(canvas);

    // Offline overlay
    const offlineOverlay = document.createElement("div");
    offlineOverlay.className = "offline-overlay";
    offlineOverlay.textContent = "OFFLINE";

    screenContainer.appendChild(screenFrame);
    screenContainer.appendChild(offlineOverlay);

    // Footer - Action Buttons
    const footer = document.createElement("div");
    footer.className = "card-footer";

    const actions = [
      { id: "lock", label: "🔒 Lock", action: "lock" },
      { id: "unlock", label: "🔓 Unlock", action: "unlock" },
      { id: "reboot", label: "🔄 Reboot", action: "reboot" },
      { id: "poweroff", label: "⏹️ Power Off", action: "poweroff" }
    ];

    actions.forEach(({ id, label, action }) => {
      const btn = document.createElement("button");
      btn.className = `action-btn ${id}`;
      btn.textContent = label;
      btn.onclick = (e) => {
        e.stopPropagation();
        sendCommand(mac, action);
      };
      footer.appendChild(btn);
    });

    // Assemble card
    card.appendChild(header);
    card.appendChild(screenContainer);
    card.appendChild(footer);

    // Click to select
    card.addEventListener("click", (e) => {
      if (e.target.closest(".action-btn")) return;
      toggleSelectDevice(mac);
    });

    devicesGrid.appendChild(card);
    console.log("Card created and appended for MAC:", mac);
  }
  
  console.log("renderDeviceCards complete. Total cards:", devicesGrid.children.length);
}

// ============ Device Selection ============
function toggleSelectDevice(mac) {
  const card = document.querySelector(`[data-mac="${mac}"]`);
  if (!card) return;

  if (selectedDevices.has(mac)) {
    selectedDevices.delete(mac);
    card.classList.remove("selected");
  } else {
    selectedDevices.add(mac);
    card.classList.add("selected");
  }
}

function selectAllDevices() {
  selectedDevices.clear();
  document.querySelectorAll(".device-card").forEach((card) => {
    const mac = card.dataset.mac;
    selectedDevices.add(mac);
    card.classList.add("selected");
  });
}

function deselectAllDevices() {
  selectedDevices.clear();
  document.querySelectorAll(".device-card").forEach((card) => {
    card.classList.remove("selected");
  });
}

// ============ Commands ============
function sendCommand(targetMac, action) {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
    alert("Not connected to server");
    return;
  }

  connection.invoke("SendCommand", targetMac, action, null)
    .catch(err => {
      console.error("SendCommand error:", err);
      alert("Failed to send command");
    });
}

function sendCommandToSelected(action) {
  if (selectedDevices.size === 0) {
    alert("Please select one or more devices first.");
    return;
  }

  for (const mac of selectedDevices) {
    sendCommand(mac, action);
  }
}

// ============ Toolbar Events ============
if (btnSelectAll) {
  btnSelectAll.addEventListener("click", selectAllDevices);
}

if (btnLockSelected) {
  btnLockSelected.addEventListener("click", () => {
    if (selectedDevices.size === 0) {
      alert("Please select one or more devices first.");
      return;
    }
    sendCommandToSelected("lock");
  });
}

if (btnUnlockSelected) {
  btnUnlockSelected.addEventListener("click", () => {
    if (selectedDevices.size === 0) {
      alert("Please select one or more devices first.");
      return;
    }
    sendCommandToSelected("unlock");
  });
}

if (btnGlobalMessage) {
  btnGlobalMessage.addEventListener("click", () => {
    const message = prompt("Enter message for all students:");
    if (!message) return;

    if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
      alert("Not connected to server");
      return;
    }

    // Send to all devices
    for (const mac of Object.keys(devices)) {
      connection.invoke("SendCommand", mac, "message", { text: message })
        .catch(err => console.error("SendCommand error:", err));
    }
  });
}

// ============ Status Update ============
function updateDeviceStatus(mac, status) {
  const isOnline = status.toLowerCase() === "online";

  // Update internal state
  if (devices[mac]) {
    devices[mac].status = status;
    devices[mac].isOnline = isOnline;
  }

  // Update card UI
  const card = document.querySelector(`[data-mac="${mac}"]`);
  if (!card) return;

  // Update border and class
  if (isOnline) {
    card.classList.add("is-online");
  } else {
    card.classList.remove("is-online");
  }

  // Update status badge
  const badge = card.querySelector(".status-badge");
  if (badge) {
    badge.dataset.status = status;
    badge.textContent = status;
    badge.classList.remove("online", "offline");
    badge.classList.add(isOnline ? "online" : "offline");
  }
}

// ============ SignalR Connection ============
async function beginSignalR() {
  console.log("beginSignalR starting");
  
  connection = new signalR.HubConnectionBuilder()
    .withUrl("/commandhub")
    .withAutomaticReconnect()
    .build();

  connection.on("UpdateDeviceList", (deviceList) => {
    console.log("Received UpdateDeviceList event:", deviceList);
    if (Array.isArray(deviceList)) {
      renderDeviceCards(deviceList);
    } else {
      console.warn("UpdateDeviceList received but not an array:", deviceList);
    }
  });

  connection.on("DeviceStatusChanged", (mac, status) => {
    console.log(`Device ${mac} status changed to ${status}`);
    updateDeviceStatus(mac, status);
  });

  connection.onreconnecting(error => {
    console.log("SignalR reconnecting", error);
  });

  connection.onreconnected(connectionId => {
    console.log("SignalR reconnected");
  });

  connection.onclose(error => {
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

// ============ WebSocket Video Stream ============
function beginVideoSocket() {
  const protocol = window.location.protocol === "https:" ? "wss" : "ws";
  const url = `${protocol}://${window.location.host}/ws/stream?role=teacher&mac=${encodeURIComponent(TEACHER_MAC)}`;
  
  console.log("Connecting to video stream:", url);
  
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
    if (raw.length < 12) return;

    // Extract MAC (first 12 bytes) and JPEG data (rest)
    const macBytes = raw.slice(0, 12);
    const mac = new TextDecoder().decode(macBytes).trim();
    const jpegData = raw.slice(12);

    // Find the destination canvas
    const canvas = document.querySelector(`#canvas-${mac}`);
    if (!canvas) return;

    // Only render if device is online
    if (!devices[mac] || !devices[mac].isOnline) {
      return;
    }

    try {
      const blob = new Blob([jpegData], { type: "image/jpeg" });
      const imageBitmap = await createImageBitmap(blob);
      const ctx = canvas.getContext("2d");
      if (!ctx) return;

      ctx.drawImage(imageBitmap, 0, 0, canvas.width, canvas.height);
      imageBitmap.close();
    } catch (e) {
      console.warn("Video frame render failed for MAC:", mac, e);
    }
  };
}

// ============ Initialization ============
async function startApp() {
  console.log("startApp called");
  
  // Start SignalR connection first
  await beginSignalR();

  // Fetch initial device list from API as backup
  try {
    const response = await fetch("/api/devices");
    const deviceList = await response.json();
    console.log("Fetched initial device list from API:", deviceList);
    // Only render if we didn't get devices from SignalR yet
    if (Object.keys(devices).length === 0) {
      renderDeviceCards(deviceList);
    }
  } catch (err) {
    console.error("Failed to fetch initial devices:", err);
  }

  // Start WebSocket video stream
  beginVideoSocket();
}

console.log("About to check auth");

// Check authentication on page load
checkAuth();
