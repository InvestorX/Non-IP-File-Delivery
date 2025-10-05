// Current device selection
let currentDevice = 'A';

// Initialize on page load
document.addEventListener('DOMContentLoaded', function() {
    loadConfig();
    refreshInterfaces();
    refreshStatus();
    
    // Auto-refresh status every 5 seconds
    setInterval(refreshStatus, 5000);
});

// Form submission
document.getElementById('configForm').addEventListener('submit', function(e) {
    e.preventDefault();
    saveConfig();
});

// Switch between Device A and Device B
function switchDevice() {
    const selected = document.querySelector('input[name="device"]:checked').value;
    currentDevice = selected;
    
    // Update UI to show/hide device-specific fields
    const deviceAFields = document.querySelectorAll('.device-a-only');
    const deviceBFields = document.querySelectorAll('.device-b-only');
    
    if (currentDevice === 'A') {
        deviceAFields.forEach(el => el.style.display = 'block');
        deviceBFields.forEach(el => el.style.display = 'none');
    } else {
        deviceAFields.forEach(el => el.style.display = 'none');
        deviceBFields.forEach(el => el.style.display = 'block');
    }
    
    // Reload configuration for the selected device
    loadConfig();
}

// Show/hide tabs
function showTab(tabName) {
    // Hide all tabs
    document.querySelectorAll('.tab-content').forEach(tab => {
        tab.classList.remove('active');
    });
    
    // Remove active class from all buttons
    document.querySelectorAll('.tab-button').forEach(btn => {
        btn.classList.remove('active');
    });
    
    // Show selected tab
    document.getElementById('tab-' + tabName).classList.add('active');
    
    // Add active class to clicked button
    event.target.classList.add('active');
}

// Save configuration
function saveConfig() {
    const formData = new FormData(document.getElementById('configForm'));
    const config = {};
    
    // Build configuration object
    for (let [key, value] of formData.entries()) {
        config[key] = value;
    }
    
    // Add checkbox values explicitly
    config.ftpEnabled = document.getElementById('ftpEnabled').checked;
    config.sftpEnabled = document.getElementById('sftpEnabled').checked;
    config.postgresqlEnabled = document.getElementById('postgresqlEnabled').checked;
    config.enableVirusScan = document.getElementById('enableVirusScan').checked;
    config.enableDeepInspection = document.getElementById('enableDeepInspection').checked;
    config.enableZeroCopy = document.getElementById('enableZeroCopy').checked;
    
    const url = `/api/config/${currentDevice.toLowerCase()}`;
    
    fetch(url, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
        },
        body: JSON.stringify(config)
    })
    .then(response => response.json())
    .then(data => {
        if (data.success) {
            showStatus(data.message, 'success');
        } else {
            showStatus(data.message, 'error');
        }
    })
    .catch(error => {
        showStatus('設定の保存中にエラーが発生しました: ' + error.message, 'error');
    });
}

// Load configuration
function loadConfig() {
    const url = `/api/config/${currentDevice.toLowerCase()}`;
    
    fetch(url)
    .then(response => response.json())
    .then(config => {
        // General settings
        setValueIfExists('mode', config.mode);
        setValueIfExists('logLevel', config.logLevel);
        
        // Network settings
        setValueIfExists('interfaceName', config.interfaceName);
        setValueIfExists('remoteMacAddress', config.remoteMacAddress);
        setValueIfExists('frameSize', config.frameSize);
        setValueIfExists('encryption', config.encryption);
        setValueIfExists('etherType', config.etherType);
        
        // Protocol settings - FTP
        setCheckboxIfExists('ftpEnabled', config.ftpEnabled);
        setValueIfExists('ftpListenPort', config.ftpListenPort);
        setValueIfExists('ftpTargetHost', config.ftpTargetHost);
        setValueIfExists('ftpTargetPort', config.ftpTargetPort);
        
        // Protocol settings - SFTP
        setCheckboxIfExists('sftpEnabled', config.sftpEnabled);
        setValueIfExists('sftpListenPort', config.sftpListenPort);
        setValueIfExists('sftpTargetHost', config.sftpTargetHost);
        setValueIfExists('sftpTargetPort', config.sftpTargetPort);
        
        // Protocol settings - PostgreSQL
        setCheckboxIfExists('postgresqlEnabled', config.postgresqlEnabled);
        setValueIfExists('postgresqlListenPort', config.postgresqlListenPort);
        setValueIfExists('postgresqlTargetHost', config.postgresqlTargetHost);
        setValueIfExists('postgresqlTargetPort', config.postgresqlTargetPort);
        
        // Security settings
        setCheckboxIfExists('enableVirusScan', config.enableVirusScan);
        setCheckboxIfExists('enableDeepInspection', config.enableDeepInspection);
        setValueIfExists('scanTimeout', config.scanTimeout);
        setValueIfExists('quarantinePath', config.quarantinePath);
        setValueIfExists('yaraRulesPath', config.yaraRulesPath);
        
        // Performance settings
        setValueIfExists('receiveBufferSize', config.receiveBufferSize);
        setValueIfExists('maxConcurrentSessions', config.maxConcurrentSessions);
        setCheckboxIfExists('enableZeroCopy', config.enableZeroCopy);
        setValueIfExists('maxMemoryMB', config.maxMemoryMB);
        setValueIfExists('bufferSize', config.bufferSize);
        
        // Redundancy settings
        setValueIfExists('heartbeatInterval', config.heartbeatInterval);
        setValueIfExists('failoverTimeout', config.failoverTimeout);
        setValueIfExists('dataSyncMode', config.dataSyncMode);
        
        showStatus('設定を読み込みました', 'success');
    })
    .catch(error => {
        showStatus('設定の読み込み中にエラーが発生しました: ' + error.message, 'error');
    });
}

// Reset configuration to defaults
function resetConfig() {
    if (confirm('設定をデフォルト値にリセットしますか？')) {
        fetch(`/api/config/${currentDevice.toLowerCase()}/reset`, {
            method: 'POST'
        })
        .then(response => response.json())
        .then(data => {
            if (data.success) {
                loadConfig();
                showStatus('設定をデフォルト値にリセットしました', 'success');
            }
        })
        .catch(error => {
            showStatus('リセット中にエラーが発生しました: ' + error.message, 'error');
        });
    }
}

// Refresh network interfaces
function refreshInterfaces() {
    fetch('/api/interfaces')
    .then(response => response.json())
    .then(interfaces => {
        const select = document.getElementById('interfaceName');
        select.innerHTML = '';
        
        interfaces.forEach(iface => {
            const option = document.createElement('option');
            option.value = iface.name;
            option.text = `${iface.name} - ${iface.description} (${iface.macAddress})`;
            select.appendChild(option);
        });
        
        // Update local MAC address when interface is selected
        select.addEventListener('change', function() {
            const selected = interfaces.find(i => i.name === this.value);
            if (selected) {
                document.getElementById('localMac').value = selected.macAddress;
            }
        });
        
        // Set initial local MAC
        if (interfaces.length > 0) {
            const firstInterface = interfaces[0];
            document.getElementById('localMac').value = firstInterface.macAddress;
        }
    })
    .catch(error => {
        console.error('ネットワークインターフェースの読み込みに失敗:', error);
    });
}

// Refresh system status
function refreshStatus() {
    fetch('/api/status')
    .then(response => response.json())
    .then(status => {
        document.getElementById('systemStatus').textContent = status.status;
        document.getElementById('uptime').textContent = status.uptime;
        document.getElementById('throughput').textContent = status.throughput;
        document.getElementById('connections').textContent = status.connections;
        document.getElementById('memoryUsage').textContent = status.memoryUsage;
        document.getElementById('version').textContent = status.version;
        
        // Update status color
        const statusElement = document.getElementById('systemStatus');
        if (status.status === 'running') {
            statusElement.style.color = '#27ae60';
        } else if (status.status === 'stopped') {
            statusElement.style.color = '#e74c3c';
        } else {
            statusElement.style.color = '#f39c12';
        }
    })
    .catch(error => {
        console.error('ステータスの更新に失敗:', error);
    });
}

// Show status message
function showStatus(message, type) {
    const statusDiv = document.getElementById('status');
    statusDiv.innerHTML = `<div class="status ${type}">${message}</div>`;
    setTimeout(() => {
        statusDiv.innerHTML = '';
    }, 5000);
}

// Helper functions
function setValueIfExists(id, value) {
    const element = document.getElementById(id);
    if (element && value !== undefined && value !== null) {
        element.value = value;
    }
}

function setCheckboxIfExists(id, value) {
    const element = document.getElementById(id);
    if (element && value !== undefined && value !== null) {
        element.checked = value === true || value === 'true';
    }
}
