import Combine
import CoreBluetooth
import Foundation

struct HeartRateMonitorCandidate: Identifiable {
    let peripheral: CBPeripheral
    let displayName: String

    var id: UUID { peripheral.identifier }
}

enum HeartRateMonitorState: Equatable {
    case notConnected
    case scanning
    case connecting(String)
    case connected(String)
    case unavailable(String)

    var label: String {
        switch self {
        case .notConnected: "Brustgurt verbinden"
        case .scanning: "Suche nach Brustgurten …"
        case .connecting(let name): "Verbinde mit \(name) …"
        case .connected(let name): "Puls: \(name)"
        case .unavailable(let reason): reason
        }
    }
}

@MainActor
final class HeartRateMonitorManager: NSObject, ObservableObject {
    static let heartRateService = CBUUID(string: "180D")
    static let heartRateMeasurement = CBUUID(string: "2A37")

    @Published private(set) var state: HeartRateMonitorState = .notConnected
    @Published private(set) var monitors: [HeartRateMonitorCandidate] = []
    @Published private(set) var beatsPerMinute: Int?

    private var central: CBCentralManager?
    private var connectedPeripheral: CBPeripheral?

    func startScan() {
        ensureCentral()
        guard let central, central.state == .poweredOn else {
            state = .unavailable("Bluetooth ist für den Brustgurt nicht verfügbar")
            return
        }

        monitors = []
        state = .scanning
        central.scanForPeripherals(withServices: [Self.heartRateService], options: [CBCentralManagerScanOptionAllowDuplicatesKey: false])
    }

    func connect(to monitor: HeartRateMonitorCandidate) {
        central?.stopScan()
        state = .connecting(monitor.displayName)
        central?.connect(monitor.peripheral)
    }

    func disconnect() {
        if let connectedPeripheral { central?.cancelPeripheralConnection(connectedPeripheral) }
        beatsPerMinute = nil
        state = .notConnected
    }

    private func ensureCentral() {
        guard central == nil else { return }
        central = CBCentralManager(delegate: self, queue: .main)
    }
}

extension HeartRateMonitorManager: CBCentralManagerDelegate {
    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        if central.state != .poweredOn { state = .unavailable("Bluetooth ist für den Brustgurt nicht verfügbar") }
    }

    func centralManager(_ central: CBCentralManager, didDiscover peripheral: CBPeripheral, advertisementData: [String: Any], rssi RSSI: NSNumber) {
        guard !monitors.contains(where: { $0.id == peripheral.identifier }) else { return }
        let name = peripheral.name ?? (advertisementData[CBAdvertisementDataLocalNameKey] as? String) ?? "Unbekannter Brustgurt"
        monitors.append(HeartRateMonitorCandidate(peripheral: peripheral, displayName: name))
    }

    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        connectedPeripheral = peripheral
        peripheral.delegate = self
        peripheral.discoverServices([Self.heartRateService])
        state = .connected(peripheral.name ?? "Brustgurt")
    }

    func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        connectedPeripheral = nil
        beatsPerMinute = nil
        state = .notConnected
    }
}

extension HeartRateMonitorManager: CBPeripheralDelegate {
    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        guard let service = peripheral.services?.first(where: { $0.uuid == Self.heartRateService }) else { return }
        peripheral.discoverCharacteristics([Self.heartRateMeasurement], for: service)
    }

    func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        guard let measurement = service.characteristics?.first(where: { $0.uuid == Self.heartRateMeasurement }) else { return }
        peripheral.setNotifyValue(true, for: measurement)
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard characteristic.uuid == Self.heartRateMeasurement, let value = characteristic.value, value.count >= 2 else { return }
        let flags = value[value.startIndex]
        let usesUInt16 = flags & 0x01 != 0
        if usesUInt16, value.count >= 3 {
            beatsPerMinute = Int(value[value.startIndex + 1]) | (Int(value[value.startIndex + 2]) << 8)
        } else {
            beatsPerMinute = Int(value[value.startIndex + 1])
        }
    }
}
