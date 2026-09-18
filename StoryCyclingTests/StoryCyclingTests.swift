import XCTest
@testable import StoryCycling

final class StoryCyclingTests: XCTestCase {
    func testConnectedStateHasReadableLabel() {
        XCTAssertEqual(TrainerConnectionState.connected("KICKR CORE").label, "Verbunden: KICKR CORE")
    }

    func testBluetoothOffStateExplainsTheProblem() {
        XCTAssertEqual(TrainerConnectionState.bluetoothUnavailable("Bluetooth ist ausgeschaltet").label, "Bluetooth ist ausgeschaltet")
    }
}
