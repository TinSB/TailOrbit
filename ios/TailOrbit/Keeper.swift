import CoreLocation

/// iPhone 上的 App 退到后台很快就被暂停，除非在放声音、导航之类。电脑没出声、没复制时也得在后台等着，就开一个最粗的定位撑着：
/// 3 公里精度、走多远都不回报，几乎不费电；位置本身不用、不存、不发。权限先要“使用 App 期间”，给了再问一次要不要改成“始终”
/// （“始终”在后台不显示蓝色定位标记；不改也能用）。收听、剪贴板哪个开着都要撑着，两个都关了才停。都在主线程上。
final class Keeper: NSObject, CLLocationManagerDelegate {
    static let shared = Keeper()
    private let manager = CLLocationManager()
    private var session: CLBackgroundActivitySession?
    private var holders = Set<String>()
    private var running: Bool { !holders.isEmpty }
    var onDenied: ((Bool) -> Void)?   // 定位被关掉了：界面提示去设置里打开

    override init() {
        super.init()
        manager.delegate = self
        manager.desiredAccuracy = kCLLocationAccuracyThreeKilometers
        manager.distanceFilter = CLLocationDistanceMax
        manager.pausesLocationUpdatesAutomatically = false
        manager.allowsBackgroundLocationUpdates = true
        manager.showsBackgroundLocationIndicator = false
    }

    // who 要（on）、不要了（!on）后台一直开着
    func hold(_ who: String, _ on: Bool) {
        if on { holders.insert(who) } else { holders.remove(who) }
        apply()
    }

    private func apply() {
        let status = manager.authorizationStatus
        onDenied?(running && (status == .denied || status == .restricted))
        guard running else {
            manager.stopUpdatingLocation()
            session?.invalidate()
            session = nil
            return
        }
        switch status {
        case .notDetermined:
            manager.requestWhenInUseAuthorization()
        case .authorizedWhenInUse, .authorizedAlways:
            if status == .authorizedWhenInUse && !UserDefaults.standard.bool(forKey: "askedAlways") {
                UserDefaults.standard.set(true, forKey: "askedAlways")
                manager.requestAlwaysAuthorization()
            }
            if session == nil { session = CLBackgroundActivitySession() }
            manager.startUpdatingLocation()
        default:
            break
        }
    }

    func locationManagerDidChangeAuthorization(_ manager: CLLocationManager) { apply() }
    func locationManager(_ manager: CLLocationManager, didUpdateLocations locations: [CLLocation]) {}
    func locationManager(_ manager: CLLocationManager, didFailWithError error: Error) {}
}
