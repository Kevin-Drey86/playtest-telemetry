// Reads NSProcessInfo.thermalState for the Playtest Telemetry package.
// 0 = Nominal, 1 = Fair, 2 = Serious, 3 = Critical.
#import <Foundation/Foundation.h>

extern "C" {
    int PT_GetThermalState(void) {
        return (int)[NSProcessInfo processInfo].thermalState;
    }
}
