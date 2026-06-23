// boltmate_un.m
//
// Tiny Objective-C sidecar that wraps the three UNUserNotificationCenter
// calls BoltMate needs in plain C functions. Lets the .NET host call into
// the modern notifications API without ever touching the Objective-C
// block ABI from managed code — blocks live entirely on this side, and
// our completion handlers signal a semaphore that the C function waits
// on before returning. The result is a fully synchronous C API that
// P/Invoke can hit safely from .NET.
//
// Build (clang, ARM64):
//   clang -arch arm64 -fobjc-arc -dynamiclib \
//         -framework Foundation -framework UserNotifications \
//         -o libboltmate_un.dylib boltmate_un.m
//
// Built into the .app bundle by Directory.Build.targets's
// StageMacAppBundle step. Lives next to the executable in
// Contents/MacOS/ so the default dlopen search path picks it up.
//
// API contract:
//
//   int bm_un_get_status(void);
//     Synchronously returns the current authorization status — values
//     match UNAuthorizationStatus exactly (0 NotDetermined, 1 Denied,
//     2 Authorized, 3 Provisional, 4 Ephemeral). Returns -1 on error.
//     2 second timeout if the OS callback never fires.
//
//   int bm_un_request_authorization(int options);
//     Synchronously drives UNUserNotificationCenter.requestAuthorization
//     which shows the OS modal Allow / Don't Allow prompt on first call.
//     `options` is a bitmask matching UNAuthorizationOptions
//     (1 Badge, 2 Sound, 4 Alert). Returns 1 if granted, 0 if denied,
//     -1 on error. 30 second timeout (user might be slow on the prompt).
//
//   int bm_un_deliver(const char* title, const char* body);
//     Posts a UNNotificationRequest immediately. Returns 1 on dispatch
//     success, 0 on parameter / encoding failure. Does NOT wait for the
//     completion handler — the OS auth-gates the delivery, so a denied
//     status will silently drop the post without us caring here.

#import <Foundation/Foundation.h>
#import <UserNotifications/UserNotifications.h>

// ---------------------------------------------------------------
// Status probe
// ---------------------------------------------------------------

int bm_un_get_status(void) {
    __block int result = -1;
    dispatch_semaphore_t sem = dispatch_semaphore_create(0);

    UNUserNotificationCenter* center = [UNUserNotificationCenter currentNotificationCenter];
    if (center == nil) {
        return -1;
    }

    [center getNotificationSettingsWithCompletionHandler:^(UNNotificationSettings* settings) {
        if (settings != nil) {
            result = (int)settings.authorizationStatus;
        }
        dispatch_semaphore_signal(sem);
    }];

    // 2s cap — the callback typically fires in <50ms, but a wedged
    // notification daemon shouldn't block our caller indefinitely.
    dispatch_time_t timeout = dispatch_time(DISPATCH_TIME_NOW, 2LL * NSEC_PER_SEC);
    if (dispatch_semaphore_wait(sem, timeout) != 0) {
        return -1;
    }
    return result;
}

// ---------------------------------------------------------------
// Request authorization (shows modal)
// ---------------------------------------------------------------

int bm_un_request_authorization(int options) {
    __block int result = -1;
    dispatch_semaphore_t sem = dispatch_semaphore_create(0);

    UNUserNotificationCenter* center = [UNUserNotificationCenter currentNotificationCenter];
    if (center == nil) {
        return -1;
    }

    UNAuthorizationOptions opts = (UNAuthorizationOptions)options;
    [center requestAuthorizationWithOptions:opts
                          completionHandler:^(BOOL granted, NSError* _Nullable error) {
        if (error != nil) {
            // If the OS reports an error we still want to surface "denied"
            // rather than -1 so the caller treats it as a clean rejection.
            result = 0;
        } else {
            result = granted ? 1 : 0;
        }
        dispatch_semaphore_signal(sem);
    }];

    // 30s cap — user has to dismiss the modal. If they leave it sitting
    // we'd rather return "denied" than block our process forever.
    dispatch_time_t timeout = dispatch_time(DISPATCH_TIME_NOW, 30LL * NSEC_PER_SEC);
    if (dispatch_semaphore_wait(sem, timeout) != 0) {
        return -1;
    }
    return result;
}

// ---------------------------------------------------------------
// Deliver notification
// ---------------------------------------------------------------

int bm_un_deliver(const char* title, const char* body) {
    if (title == NULL || body == NULL) return 0;

    @autoreleasepool {
        NSString* nsTitle = [NSString stringWithUTF8String:title];
        NSString* nsBody  = [NSString stringWithUTF8String:body];
        if (nsTitle == nil || nsBody == nil) return 0;

        UNUserNotificationCenter* center = [UNUserNotificationCenter currentNotificationCenter];
        if (center == nil) return 0;

        UNMutableNotificationContent* content = [[UNMutableNotificationContent alloc] init];
        content.title = nsTitle;
        content.body  = nsBody;
        content.sound = [UNNotificationSound defaultSound];

        NSString* identifier = [[NSUUID UUID] UUIDString];
        UNNotificationRequest* request = [UNNotificationRequest
                                          requestWithIdentifier:identifier
                                                        content:content
                                                        trigger:nil];

        // Fire-and-forget — the OS gates this against authorization
        // status and drops it silently if denied. Errors here aren't
        // something we can act on (we already checked status before
        // calling), so log them server-side via the OS if needed.
        [center addNotificationRequest:request
                 withCompletionHandler:^(NSError* _Nullable error) {
            (void)error;
        }];
    }
    return 1;
}
