import Combine
import CoreBluetooth
import Foundation

enum ZwiftClickState: Equatable {
    case idle, scanning, connecting, connected, unavailable(String)
    var label: String { switch self {
    case .idle: "Zwift Click verbinden"; case .scanning: "Suche nach Zwift Click …"; case .connecting: "Verbinde mit Zwift Click …"; case .connected: "Zwift Click verbunden"; case .unavailable(let message): message } }
}

enum ShiftDirection { case up, down }

@MainActor
final class ZwiftClickManager: NSObject, ObservableObject {
    private static let service = CBUUID(string: "00000001-19CA-4651-86E5-FA29DCDD09D1")
    private static let asyncNotification = CBUUID(string: "00000002-19CA-4651-86E5-FA29DCDD09D1")
    private static let syncRX = CBUUID(string: "00000003-19CA-4651-86E5-FA29DCDD09D1")
    @Published private(set) var state: ZwiftClickState = .idle
    @Published private(set) var lastDirection: ShiftDirection?
    @Published private(set) var shiftEventCounter = 0
    private var central: CBCentralManager?
    private var peripheral: CBPeripheral?
    private var syncRX: CBCharacteristic?
    private var plusPressed = false
    private var minusPressed = false
    private var scanRequested = false

    func connect() {
        scanRequested = true
        if central == nil {
            central = CBCentralManager(delegate: self, queue: .main)
            state = .scanning
            return
        }
        guard let central, central.state == .poweredOn else {
            state = .scanning
            return
        }
        beginScan(with: central)
    }

    private func beginScan(with central: CBCentralManager) {
        state = .scanning
        central.scanForPeripherals(withServices: nil, options: nil)
    }
    func disconnect() {
        if let peripheral { central?.cancelPeripheralConnection(peripheral) }
        peripheral = nil
        syncRX = nil
        plusPressed = false
        minusPressed = false
        scanRequested = false
        state = .idle
    }
    private func shift(_ direction: ShiftDirection) { lastDirection = direction; shiftEventCounter += 1 }
}

extension ZwiftClickManager: CBCentralManagerDelegate {
    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        guard central.state == .poweredOn else {
            state = .unavailable("Bluetooth ist für Zwift Click nicht verfügbar")
            return
        }
        if scanRequested { beginScan(with: central) }
    }
    func centralManager(_ central: CBCentralManager, didDiscover peripheral: CBPeripheral, advertisementData: [String: Any], rssi RSSI: NSNumber) {
        let name = peripheral.name ?? (advertisementData[CBAdvertisementDataLocalNameKey] as? String) ?? ""
        guard name.localizedCaseInsensitiveContains("Zwift Click") else { return }
        central.stopScan(); self.peripheral = peripheral; state = .connecting; central.connect(peripheral)
    }
    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) { peripheral.delegate = self; peripheral.discoverServices([Self.service]) }
    func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) { state = .idle }
}

extension ZwiftClickManager: CBPeripheralDelegate {
    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        if let service = peripheral.services?.first(where: { $0.uuid == Self.service }) {
            peripheral.discoverCharacteristics([Self.asyncNotification, Self.syncRX], for: service)
        } else {
            state = .unavailable("Dieser Click verwendet ein nicht unterstütztes BLE-Protokoll")
        }
    }
    func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        for characteristic in service.characteristics ?? [] {
            if characteristic.uuid == Self.asyncNotification { peripheral.setNotifyValue(true, for: characteristic) }
            if characteristic.uuid == Self.syncRX { syncRX = characteristic }
        }
        if let syncRX { peripheral.writeValue(Data("RideOn".utf8), for: syncRX, type: .withResponse); state = .connected }
    }
    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard characteristic.uuid == Self.asyncNotification, let data = characteristic.value, data.count >= 5, data[data.startIndex] == 55 else { return }
        let bytes = Array(data.dropFirst())
        var plus: UInt8 = 1; var minus: UInt8 = 1; var index = 0
        while index + 1 < bytes.count { let field = bytes[index]; let value = bytes[index + 1]; if field == 8 { plus = value }; if field == 16 { minus = value }; index += 2 }
        let newPlusPressed = plus == 0; let newMinusPressed = minus == 0
        if newPlusPressed && !plusPressed { shift(.up) }; if newMinusPressed && !minusPressed { shift(.down) }
        plusPressed = newPlusPressed; minusPressed = newMinusPressed
    }
}
