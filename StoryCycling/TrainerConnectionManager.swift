import CoreBluetooth
import Combine
import Foundation

struct TrainerCandidate: Identifiable {
    let peripheral: CBPeripheral
    let displayName: String

    var id: UUID { peripheral.identifier }
}

enum TrainerConnectionState: Equatable {
    case notRequested
    case bluetoothUnavailable(String)
    case ready
    case scanning
    case connecting(String)
    case connected(String)
    case disconnected
    case failed(String)

    var label: String {
        switch self {
        case .notRequested: "KICKR verbinden"
        case .bluetoothUnavailable(let reason): reason
        case .ready: "Bereit zum Suchen"
        case .scanning: "Suche nach Trainern …"
        case .connecting(let name): "Verbinde mit \(name) …"
        case .connected(let name): "Verbunden: \(name)"
        case .disconnected: "Trainer getrennt"
        case .failed(let reason): reason
        }
    }

    var isConnected: Bool {
        if case .connected = self { return true }
        return false
    }
}

@MainActor
final class TrainerConnectionManager: NSObject, ObservableObject {
    static let fitnessMachineService = CBUUID(string: "1826")
    static let fitnessMachineControlPoint = CBUUID(string: "2AD9")
    static let supportedResistanceLevelRange = CBUUID(string: "2AD6")

    @Published private(set) var state: TrainerConnectionState = .notRequested
    @Published private(set) var trainers: [TrainerCandidate] = []
    @Published private(set) var resistanceRange: ClosedRange<Double>?
    @Published private(set) var currentResistance: Double?
    @Published private(set) var resistanceError: String?

    private var central: CBCentralManager?
    private var connectedPeripheral: CBPeripheral?
    private var intentionallyDisconnected = false
    private var controlPoint: CBCharacteristic?
    private var pendingResistance: Int16?

    func startScan() {
        ensureCentralManager()
        guard let central else { return }

        guard central.state == .poweredOn else {
            state = .bluetoothUnavailable(reason(for: central.state))
            return
        }

        trainers = []
        intentionallyDisconnected = false
        state = .scanning

        // Scan broadly: older trainer firmware may not advertise FTMS until after a connection.
        central.scanForPeripherals(
            withServices: nil,
            options: [CBCentralManagerScanOptionAllowDuplicatesKey: false]
        )
    }

    func connect(to trainer: TrainerCandidate) {
        ensureCentralManager()
        guard let central else { return }

        intentionallyDisconnected = false
        central.stopScan()
        state = .connecting(trainer.displayName)
        central.connect(trainer.peripheral, options: [CBConnectPeripheralOptionNotifyOnDisconnectionKey: true])
    }

    func disconnect() {
        intentionallyDisconnected = true
        central?.stopScan()

        if let connectedPeripheral {
            central?.cancelPeripheralConnection(connectedPeripheral)
        } else {
            state = .disconnected
        }
    }

    func setResistance(_ level: Double) {
        guard let range = resistanceRange, let controlPoint, let peripheral = connectedPeripheral else { return }
        let clamped = min(max(level, range.lowerBound), range.upperBound)
        pendingResistance = Int16((clamped * 10).rounded())
        resistanceError = nil
        peripheral.writeValue(Data([0x00]), for: controlPoint, type: .withResponse) // Request Control
    }

    private func ensureCentralManager() {
        guard central == nil else { return }
        central = CBCentralManager(delegate: self, queue: .main)
    }

    private func isLikelyTrainer(_ peripheral: CBPeripheral, advertisementData: [String: Any]) -> Bool {
        let name = (peripheral.name ?? advertisementData[CBAdvertisementDataLocalNameKey] as? String ?? "").uppercased()
        let advertisedServices = advertisementData[CBAdvertisementDataServiceUUIDsKey] as? [CBUUID] ?? []
        return name.contains("KICKR") || advertisedServices.contains(Self.fitnessMachineService)
    }

    private func reason(for state: CBManagerState) -> String {
        switch state {
        case .unauthorized: "Bluetooth-Zugriff nicht erlaubt"
        case .unsupported: "Bluetooth LE wird auf diesem Gerät nicht unterstützt"
        case .poweredOff: "Bluetooth ist ausgeschaltet"
        case .resetting: "Bluetooth startet gerade neu"
        case .unknown: "Bluetooth-Status wird geprüft"
        case .poweredOn: "Bereit zum Suchen"
        @unknown default: "Bluetooth ist derzeit nicht verfügbar"
        }
    }
}

extension TrainerConnectionManager: CBCentralManagerDelegate {
    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        if central.state == .poweredOn {
            if case .bluetoothUnavailable = state { state = .ready }
        } else {
            state = .bluetoothUnavailable(reason(for: central.state))
        }
    }

    func centralManager(_ central: CBCentralManager, didDiscover peripheral: CBPeripheral, advertisementData: [String: Any], rssi RSSI: NSNumber) {
        guard isLikelyTrainer(peripheral, advertisementData: advertisementData) else { return }
        guard !trainers.contains(where: { $0.id == peripheral.identifier }) else { return }

        let name = peripheral.name ?? (advertisementData[CBAdvertisementDataLocalNameKey] as? String) ?? "Unbekannter Trainer"
        trainers.append(TrainerCandidate(peripheral: peripheral, displayName: name))
    }

    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        connectedPeripheral = peripheral
        peripheral.delegate = self
        peripheral.discoverServices([Self.fitnessMachineService])
        state = .connected(peripheral.name ?? "KICKR Core")
    }

    func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
        state = .failed(error?.localizedDescription ?? "Verbindung zum Trainer fehlgeschlagen")
    }

    func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        connectedPeripheral = nil
        controlPoint = nil
        resistanceRange = nil
        currentResistance = nil
        state = .disconnected

        if !intentionallyDisconnected {
            startScan()
        }
    }
}

extension TrainerConnectionManager: CBPeripheralDelegate {
    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        guard let service = peripheral.services?.first(where: { $0.uuid == Self.fitnessMachineService }) else { return }
        peripheral.discoverCharacteristics([Self.fitnessMachineControlPoint, Self.supportedResistanceLevelRange], for: service)
    }

    func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        for characteristic in service.characteristics ?? [] {
            switch characteristic.uuid {
            case Self.fitnessMachineControlPoint:
                controlPoint = characteristic
                peripheral.setNotifyValue(true, for: characteristic)
            case Self.supportedResistanceLevelRange:
                peripheral.readValue(for: characteristic)
            default:
                break
            }
        }
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard error == nil, let value = characteristic.value else { return }

        if characteristic.uuid == Self.supportedResistanceLevelRange, value.count >= 6 {
            func int16(at offset: Int) -> Int16 {
                Int16(bitPattern: UInt16(value[value.startIndex + offset]) | (UInt16(value[value.startIndex + offset + 1]) << 8))
            }
            let minimum = Double(int16(at: 0)) / 10
            let maximum = Double(int16(at: 2)) / 10
            if minimum <= maximum { resistanceRange = minimum...maximum }
        }

        if characteristic.uuid == Self.fitnessMachineControlPoint, value.count >= 3, value[value.startIndex] == 0x80 {
            let requestOpcode = value[value.startIndex + 1]
            let succeeded = value[value.startIndex + 2] == 0x01
            if requestOpcode == 0x00, succeeded, let pendingResistance, let controlPoint {
                let raw = UInt16(bitPattern: pendingResistance)
                let command = Data([0x04, UInt8(raw & 0xFF), UInt8(raw >> 8)])
                peripheral.writeValue(command, for: controlPoint, type: .withResponse)
            } else if requestOpcode == 0x04, succeeded, let pendingResistance {
                currentResistance = Double(pendingResistance) / 10
                self.pendingResistance = nil
            } else if !succeeded {
                resistanceError = "Trainer hat den Widerstands-Befehl abgelehnt"
                pendingResistance = nil
            }
        }
    }
}
