#import <Foundation/Foundation.h>
#import <CoreBluetooth/CoreBluetooth.h>

extern "C" void UnitySendMessage(const char*, const char*, const char*);

@interface CCDevices : NSObject<CBCentralManagerDelegate, CBPeripheralDelegate>
@property(nonatomic,strong) CBCentralManager *central;
@property(nonatomic,strong) NSMutableDictionary<NSString*,CBPeripheral*> *found;
@property(nonatomic,strong) NSMutableDictionary<NSString*,NSString*> *roles;
@property(nonatomic,strong) NSMutableDictionary<NSString*,CBPeripheral*> *selected;
@property(nonatomic,copy) NSString *target;
@property(nonatomic,copy) NSString *scanRole;
@property(nonatomic) NSUInteger scanGeneration;
// FTMS-Steuerkanal (Fitness Machine Control Point 2AD9): Befehle nacheinander, jeweils nach der Antwort-Indication; Sollwerte (Steigung/Watt) werden zusammengefasst
@property(nonatomic,strong) CBCharacteristic *controlPoint;
@property(nonatomic,strong) NSMutableArray<NSData*> *controlQueue;
@property(nonatomic) BOOL controlBusy;
@property(nonatomic) NSUInteger controlGeneration;
// Zwift Click: Tasten kommen als Notification auf 00000002, Handshake "RideOn" auf 00000003
@property(nonatomic,strong) CBCharacteristic *clickSync;
- (void)trainerCommand:(NSData*)command;
- (void)scan:(NSString*)role;
- (void)connect:(NSString*)identifier role:(NSString*)role;
- (void)disconnect:(NSString*)role;
@end
@implementation CCDevices
static NSString *const ClickService=@"00000001-19CA-4651-86E5-FA29DCDD09D1", *const ClickAsync=@"00000002-19CA-4651-86E5-FA29DCDD09D1", *const ClickSync=@"00000003-19CA-4651-86E5-FA29DCDD09D1";
- (instancetype)init {
    if((self=[super init])) { _found=[NSMutableDictionary new]; _roles=[NSMutableDictionary new]; _selected=[NSMutableDictionary new]; _controlQueue=[NSMutableArray new]; }
    return self;
}
- (void)emit:(NSDictionary*)event {
    if(!_target.length)return;
    NSData *data=[NSJSONSerialization dataWithJSONObject:event options:0 error:nil];
    NSString *json=[[NSString alloc]initWithData:data encoding:NSUTF8StringEncoding];
    UnitySendMessage(_target.UTF8String,"OnBluetoothEvent",json.UTF8String);
}
- (void)state:(NSString*)state role:(NSString*)role peripheral:(CBPeripheral*)p {
    if(!role)return;
    [self emit:@{@"type":@"state",@"role":role,@"state":state,@"name":p.name?:@""}];
}
- (void)scan:(NSString*)role {
    if([_scanRole length]) [self state:@"Suche beendet" role:_scanRole peripheral:nil];
    _scanRole=role; _scanGeneration++;
    if(!_central)_central=[[CBCentralManager alloc]initWithDelegate:self queue:dispatch_get_main_queue()];
    if(_central.state==CBManagerStatePoweredOn)[self beginScan];
    else [self state:@"Bluetooth wird geprüft …" role:role peripheral:nil];
}
- (void)beginScan {
    if(!_scanRole)return;
    [_central stopScan];
    [self state:@"Suche läuft …" role:_scanRole peripheral:nil];
    [_central scanForPeripheralsWithServices:nil options:@{CBCentralManagerScanOptionAllowDuplicatesKey:@NO}];
    NSUInteger generation=_scanGeneration;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW,15*NSEC_PER_SEC),dispatch_get_main_queue(),^{
        if(generation!=self.scanGeneration || !self.scanRole)return;
        [self.central stopScan]; [self state:@"Suche beendet – Gerät auswählen" role:self.scanRole peripheral:nil]; self.scanRole=nil;
    });
}
- (void)centralManagerDidUpdateState:(CBCentralManager*)central {
    if(central.state==CBManagerStatePoweredOn) { [self beginScan]; return; }
    NSString *reason=central.state==CBManagerStateUnauthorized?@"Bluetooth in Einstellungen erlauben":central.state==CBManagerStatePoweredOff?@"Bluetooth ist ausgeschaltet":@"Bluetooth nicht verfügbar";
    [self state:reason role:@"trainer" peripheral:nil]; [self state:reason role:@"heart" peripheral:nil];
    for(CBPeripheral *p in _selected.allValues) [_central cancelPeripheralConnection:p];
    [_selected removeAllObjects];
}
- (void)centralManager:(CBCentralManager*)central didDiscoverPeripheral:(CBPeripheral*)p advertisementData:(NSDictionary*)ad RSSI:(NSNumber*)rssi {
    if(!_scanRole)return;
    NSString *name=ad[CBAdvertisementDataLocalNameKey]?:p.name?:@"Bluetooth-Gerät";
    NSArray *services=ad[CBAdvertisementDataServiceUUIDsKey]?:@[];
    BOOL match;
    if([_scanRole isEqualToString:@"trainer"]) match=[name.uppercaseString containsString:@"KICKR"]||[services containsObject:[CBUUID UUIDWithString:@"1826"]];
    else if([_scanRole isEqualToString:@"click"]) match=[name.uppercaseString containsString:@"ZWIFT CLICK"]||[services containsObject:[CBUUID UUIDWithString:@"FC82"]]||[services containsObject:[CBUUID UUIDWithString:ClickService]];
    else match=[services containsObject:[CBUUID UUIDWithString:@"180D"]];
    if(!match)return;
    NSString *identifier=p.identifier.UUIDString; _found[identifier]=p;
    [self emit:@{@"type":@"candidate",@"role":_scanRole,@"id":identifier,@"name":name}];
}
- (void)connect:(NSString*)identifier role:(NSString*)role {
    CBPeripheral *p=_found[identifier]; if(!p)return;
    if(_selected[role]==p && p.state==CBPeripheralStateConnected)return;
    [self disconnect:role]; [_central stopScan]; _scanRole=nil; _scanGeneration++;
    _selected[role]=p; _roles[identifier]=role;
    [self state:@"Verbinde …" role:role peripheral:p]; [_central connectPeripheral:p options:nil];
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW,12*NSEC_PER_SEC),dispatch_get_main_queue(),^{
        if(self.selected[role]==p && p.state==CBPeripheralStateConnecting) {
            [self.central cancelPeripheralConnection:p]; [self state:@"Verbindung abgelaufen – erneut suchen" role:role peripheral:p];
        }
    });
}
- (void)resetControl {
    _controlPoint=nil; [_controlQueue removeAllObjects]; _controlBusy=NO; _controlGeneration++;
}
- (void)disconnect:(NSString*)role {
    if([role isEqualToString:@"trainer"]) [self resetControl];
    if([role isEqualToString:@"click"]) _clickSync=nil;
    if([_scanRole isEqualToString:role]) { [_central stopScan]; _scanRole=nil; _scanGeneration++; }
    CBPeripheral *p=_selected[role]; [_selected removeObjectForKey:role];
    if(p)[_central cancelPeripheralConnection:p];
    [self state:@"Getrennt" role:role peripheral:nil];
}
- (NSString*)activeRole:(CBPeripheral*)p {
    NSString *role=_roles[p.identifier.UUIDString]; return role && _selected[role]==p?role:nil;
}
- (void)centralManager:(CBCentralManager*)central didConnectPeripheral:(CBPeripheral*)p {
    NSString *role=[self activeRole:p]; if(!role)return;
    p.delegate=self;
    [self state:@"Datenkanal wird geprüft …" role:role peripheral:p];
    NSString *service=[role isEqualToString:@"trainer"]?@"1826":[role isEqualToString:@"click"]?ClickService:@"180D";
    [p discoverServices:@[[CBUUID UUIDWithString:service]]];
}
- (void)centralManager:(CBCentralManager*)central didFailToConnectPeripheral:(CBPeripheral*)p error:(NSError*)error {
    NSString *role=[self activeRole:p]; if(!role)return;
    [self state:@"Verbindung fehlgeschlagen – erneut suchen" role:role peripheral:p]; [_selected removeObjectForKey:role];
}
- (void)centralManager:(CBCentralManager*)central didDisconnectPeripheral:(CBPeripheral*)p error:(NSError*)error {
    NSString *role=[self activeRole:p]; if(!role)return;
    if([role isEqualToString:@"trainer"]) [self resetControl];
    [self state:@"Verbindung getrennt – erneut suchen" role:role peripheral:p]; [_selected removeObjectForKey:role];
}
- (void)peripheral:(CBPeripheral*)p didDiscoverServices:(NSError*)error {
    NSString *role=[self activeRole:p]; if(!role)return;
    if(error || p.services.count==0) { [self state:@"Passender Datenservice fehlt" role:role peripheral:p];return; }
    NSArray *uuids=[role isEqualToString:@"trainer"]?@[[CBUUID UUIDWithString:@"2AD2"],[CBUUID UUIDWithString:@"2AD9"]]
                 :[role isEqualToString:@"click"]?@[[CBUUID UUIDWithString:ClickAsync],[CBUUID UUIDWithString:ClickSync]]:@[[CBUUID UUIDWithString:@"2A37"]];
    for(CBService *service in p.services) [p discoverCharacteristics:uuids forService:service];
}
- (void)peripheral:(CBPeripheral*)p didDiscoverCharacteristicsForService:(CBService*)service error:(NSError*)error {
    NSString *role=[self activeRole:p]; if(!role)return;
    if(error || service.characteristics.count==0) { [self state:@"Messwerte werden nicht unterstützt" role:role peripheral:p];return; }
    for(CBCharacteristic *c in service.characteristics) {
        BOOL control=[c.UUID isEqual:[CBUUID UUIDWithString:@"2AD9"]];
        if([c.UUID isEqual:[CBUUID UUIDWithString:ClickSync]]) { _clickSync=c; continue; }
        if(c.properties & (CBCharacteristicPropertyNotify|CBCharacteristicPropertyIndicate)) [p setNotifyValue:YES forCharacteristic:c];
        else if(control) [self emit:@{@"type":@"control",@"role":role,@"state":@"Trainer ohne Steuerkanal"}];
        else [self state:@"Live-Datenkanal fehlt" role:role peripheral:p];
    }
}
- (void)peripheral:(CBPeripheral*)p didUpdateNotificationStateForCharacteristic:(CBCharacteristic*)c error:(NSError*)error {
    NSString *role=[self activeRole:p]; if(!role)return;
    if([c.UUID isEqual:[CBUUID UUIDWithString:@"2AD9"]]) {
        if(error||!c.isNotifying) { [self emit:@{@"type":@"control",@"role":role,@"state":@"Steuerkanal nicht verfügbar"}]; return; }
        [self resetControl]; _controlPoint=c;
        [self emit:@{@"type":@"control",@"role":role,@"state":@"Steuerung wird angefragt …"}];
        uint8_t request=0x00, start=0x07;                                       // Request Control, Start/Resume
        [self trainerCommand:[NSData dataWithBytes:&request length:1]];
        [self trainerCommand:[NSData dataWithBytes:&start length:1]];
        return;
    }
    if([c.UUID isEqual:[CBUUID UUIDWithString:ClickAsync]] && !error && c.isNotifying) {
        if(!_clickSync) { [self state:@"Click: Handshake-Kanal fehlt" role:role peripheral:p]; return; }
        [p writeValue:[@"RideOn" dataUsingEncoding:NSUTF8StringEncoding] forCharacteristic:_clickSync type:CBCharacteristicWriteWithResponse];
    }
    [self state:(!error&&c.isNotifying)?@"Bereit":@"Live-Daten konnten nicht aktiviert werden" role:role peripheral:p];
}
// Befehl einreihen: Sollwerte (0x05 Watt, 0x11 Simulation) ersetzen einen noch wartenden Befehl desselben Typs
- (void)trainerCommand:(NSData*)command {
    if(!command.length)return;
    uint8_t op=((const uint8_t*)command.bytes)[0];
    if(op==0x05||op==0x11) {
        for(NSInteger i=(NSInteger)_controlQueue.count-1;i>=0;i--) if(((const uint8_t*)_controlQueue[i].bytes)[0]==op) [_controlQueue removeObjectAtIndex:i];
    }
    [_controlQueue addObject:command];
    [self pumpControl];
}
- (void)pumpControl {
    if(_controlBusy || !_controlPoint || _controlQueue.count==0)return;
    CBPeripheral *p=_selected[@"trainer"]; if(!p || p.state!=CBPeripheralStateConnected)return;
    NSData *next=_controlQueue.firstObject; [_controlQueue removeObjectAtIndex:0];
    _controlBusy=YES; NSUInteger generation=++_controlGeneration;
    [p writeValue:next forCharacteristic:_controlPoint type:CBCharacteristicWriteWithResponse];
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW,(int64_t)(1.5*NSEC_PER_SEC)),dispatch_get_main_queue(),^{   // keine Antwort: weiter
        if(generation!=self.controlGeneration || !self.controlBusy)return;
        self.controlBusy=NO; [self pumpControl];
    });
}
- (void)peripheral:(CBPeripheral*)p didWriteValueForCharacteristic:(CBCharacteristic*)c error:(NSError*)error {
    if(![c.UUID isEqual:[CBUUID UUIDWithString:@"2AD9"]] || !error)return;
    _controlBusy=NO; _controlGeneration++;
    [self emit:@{@"type":@"control",@"role":@"trainer",@"state":@"Befehl nicht angenommen"}];
    [self pumpControl];
}
- (void)peripheral:(CBPeripheral*)p didUpdateValueForCharacteristic:(CBCharacteristic*)c error:(NSError*)error {
    NSString *role=[self activeRole:p]; if(!role || error || !c.value)return;
    if([c.UUID isEqual:[CBUUID UUIDWithString:@"2AD9"]]) { _controlBusy=NO; _controlGeneration++; dispatch_async(dispatch_get_main_queue(),^{[self pumpControl];}); }
    [self emit:@{@"type":@"data",@"role":role,@"characteristic":c.UUID.UUIDString,@"data":[c.value base64EncodedStringWithOptions:0]}];
}
@end
static CCDevices *bridge;
extern "C" void CCDevicesInit(const char *target) {
    NSString *t=[NSString stringWithUTF8String:target]; dispatch_async(dispatch_get_main_queue(),^{if(!bridge)bridge=[CCDevices new]; bridge.target=t;});
}
extern "C" void CCDevicesScan(const char *role) { NSString *r=[NSString stringWithUTF8String:role]; dispatch_async(dispatch_get_main_queue(),^{[bridge scan:r];}); }
extern "C" void CCDevicesConnect(const char *identifier,const char *role) { NSString *i=[NSString stringWithUTF8String:identifier], *r=[NSString stringWithUTF8String:role]; dispatch_async(dispatch_get_main_queue(),^{[bridge connect:i role:r];}); }
extern "C" void CCDevicesDisconnect(const char *role) { NSString *r=[NSString stringWithUTF8String:role]; dispatch_async(dispatch_get_main_queue(),^{[bridge disconnect:r];}); }
extern "C" void CCDevicesTrainerCommand(const char *base64) {
    NSData *d=[[NSData alloc]initWithBase64EncodedString:[NSString stringWithUTF8String:base64] options:0];
    dispatch_async(dispatch_get_main_queue(),^{ if(d) [bridge trainerCommand:d]; });
}
extern "C" void CCDevicesShutdown() { dispatch_async(dispatch_get_main_queue(),^{[bridge.central stopScan];bridge.scanRole=nil;bridge.scanGeneration++;[bridge disconnect:@"trainer"];[bridge disconnect:@"heart"];[bridge disconnect:@"click"];bridge.target=nil;}); }
