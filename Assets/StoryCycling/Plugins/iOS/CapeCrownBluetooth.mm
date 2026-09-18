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
- (void)scan:(NSString*)role;
- (void)connect:(NSString*)identifier role:(NSString*)role;
- (void)disconnect:(NSString*)role;
@end
@implementation CCDevices
- (instancetype)init {
    if((self=[super init])) { _found=[NSMutableDictionary new]; _roles=[NSMutableDictionary new]; _selected=[NSMutableDictionary new]; }
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
    BOOL trainer=[_scanRole isEqualToString:@"trainer"];
    BOOL match=trainer?([name.uppercaseString containsString:@"KICKR"]||[services containsObject:[CBUUID UUIDWithString:@"1826"]]):[services containsObject:[CBUUID UUIDWithString:@"180D"]];
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
- (void)disconnect:(NSString*)role {
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
    [p discoverServices:@[[CBUUID UUIDWithString:[role isEqualToString:@"trainer"]?@"1826":@"180D"]]];
}
- (void)centralManager:(CBCentralManager*)central didFailToConnectPeripheral:(CBPeripheral*)p error:(NSError*)error {
    NSString *role=[self activeRole:p]; if(!role)return;
    [self state:@"Verbindung fehlgeschlagen – erneut suchen" role:role peripheral:p]; [_selected removeObjectForKey:role];
}
- (void)centralManager:(CBCentralManager*)central didDisconnectPeripheral:(CBPeripheral*)p error:(NSError*)error {
    NSString *role=[self activeRole:p]; if(!role)return;
    [self state:@"Verbindung getrennt – erneut suchen" role:role peripheral:p]; [_selected removeObjectForKey:role];
}
- (void)peripheral:(CBPeripheral*)p didDiscoverServices:(NSError*)error {
    NSString *role=[self activeRole:p]; if(!role)return;
    if(error || p.services.count==0) { [self state:@"Passender Datenservice fehlt" role:role peripheral:p];return; }
    NSString *uuid=[role isEqualToString:@"trainer"]?@"2AD2":@"2A37";
    for(CBService *service in p.services) [p discoverCharacteristics:@[[CBUUID UUIDWithString:uuid]] forService:service];
}
- (void)peripheral:(CBPeripheral*)p didDiscoverCharacteristicsForService:(CBService*)service error:(NSError*)error {
    NSString *role=[self activeRole:p]; if(!role)return;
    if(error || service.characteristics.count==0) { [self state:@"Messwerte werden nicht unterstützt" role:role peripheral:p];return; }
    for(CBCharacteristic *c in service.characteristics) {
        if(c.properties & (CBCharacteristicPropertyNotify|CBCharacteristicPropertyIndicate)) [p setNotifyValue:YES forCharacteristic:c];
        else [self state:@"Live-Datenkanal fehlt" role:role peripheral:p];
    }
}
- (void)peripheral:(CBPeripheral*)p didUpdateNotificationStateForCharacteristic:(CBCharacteristic*)c error:(NSError*)error {
    NSString *role=[self activeRole:p]; if(!role)return;
    [self state:(!error&&c.isNotifying)?@"Bereit":@"Live-Daten konnten nicht aktiviert werden" role:role peripheral:p];
}
- (void)peripheral:(CBPeripheral*)p didUpdateValueForCharacteristic:(CBCharacteristic*)c error:(NSError*)error {
    NSString *role=[self activeRole:p]; if(!role || error || !c.value)return;
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
extern "C" void CCDevicesShutdown() { dispatch_async(dispatch_get_main_queue(),^{[bridge.central stopScan];bridge.scanRole=nil;bridge.scanGeneration++;[bridge disconnect:@"trainer"];[bridge disconnect:@"heart"];bridge.target=nil;}); }
