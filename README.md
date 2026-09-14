![GitHub Workflow Status](https://img.shields.io/github/actions/workflow/status/bgriggs/rv-circulate-water/dotnet.yml)

# RV Circulate Water
This is a service that is intended to help mitigate freezing pipes in RVs by circulating the water through the system when it gets cold.

## ConOps
This service is intended to run on a Raspberry Pi with a relay operated by GPIO.  When activated, the relay triggers a solenoid that allows water to flow back to the tank from some point in the waterline.
The service runs on a specified frequency and checks the outside temperature. At present, it uses Victron’s Cerbo RV management services and a configured temperature sensor.  

You are able to set the temperature threshold and how long to run the pump.  For example, when it is 32* F or below, activate the solenoid for 10 seconds every 15 minutes.  Additionally, it is possible to set multiple “stages” to run the pump more or less frequently depending on the temperature. For example, when it is 10* F or below, run for 20 seconds every 10 minutes.

## Monitoring and Configuration over MQTT
The service publishes its status and settings to the MQTT broker set in the `Mqtt` section of `appsettings.json`, and accepts setting changes. The Pi runs Mosquitto locally on port 1883 with anonymous access, so anyone on the RV network can read status and change settings. Leave `Mqtt:Host` empty to turn MQTT off. Changes to the `Mqtt` section take effect after a restart.

| Topic (default prefix `rv/circulate`) | Direction | Retained | Payload |
|---|---|---|---|
| `rv/circulate/availability` | Published | Yes | `online`, or `offline` when the service stops or drops off |
| `rv/circulate/status` | Published | Yes | Status JSON, sent on every change and at least every 60 seconds |
| `rv/circulate/config` | Published | Yes | The `CirculateWater` settings in effect |
| `rv/circulate/config/set` | Subscribed | No | Partial `CirculateWater` JSON with the settings to change |
| `rv/circulate/config/set/result` | Published | No | The outcome of each change |

Example status (times are UTC):
```json
{"asOf":"2026-09-13T18:42:26.01Z","startedAt":"2026-09-13T18:42:23.75Z","currentTemperatureF":72.2,"temperatureReadAt":"2026-09-13T18:42:25.69Z","activeStage":null,"solenoidIsOpen":false,"lastCirculationAt":null,"lastCirculationDurationSecs":null,"lastError":null,"lastErrorAt":null}
```
`currentTemperatureF` is `null` when the sensor can't be read, and `activeStage` is `null` when the temperature is above every stage. `lastError` and `lastErrorAt` keep the most recent error.

### Changing Settings
Publish JSON to `rv/circulate/config/set` without the retain flag, using the same names as `rv/circulate/config`:
```
mosquitto_pub -h circulate -t rv/circulate/config/set -m '{"Stage1":{"TempThresholdF":34,"CirculateDurationSecs":15}}'
```
A change is all or nothing: if any value is invalid, nothing changes. Valid changes are used from the next temperature check, with no restart. Existing stages can be edited but not added or removed. Changes published with the retain flag are rejected, so an old message can't undo later changes when the service reconnects. Empty messages, such as one clearing a retained message, are ignored. Messages larger than 64 KB are refused.

Changes are saved to `appsettings.overrides.json` next to `appsettings.json`, which holds this RV's settings. Values in `appsettings.overrides.json` take precedence, and deploys don't replace it, so changes made over MQTT survive deploys. Hand edits to `appsettings.json` have no effect on a setting that's also in `appsettings.overrides.json`. To go back to the `appsettings.json` value, remove the setting from `appsettings.overrides.json`, or delete the file to discard every change made over MQTT. The service writes the file as root, so edit it with `sudo`. If `appsettings.overrides.json` isn't valid JSON, the service ignores it and uses the `appsettings.json` values. It reports the problem in `lastError` and rejects changes until the file is fixed or deleted.

| Setting | Allowed values |
|---|---|
| `StageN.TempThresholdF` | Whole number from -60 to 120 |
| `StageN.CirculateFrequencyMins` | Whole number from 1 to 1440 |
| `StageN.CirculateDurationSecs` | Whole number from 1 to 600 |
| `CerboIP` | IP address or host name |
| `SensorVRMInstance` | Whole number from 0 to 255 |
| `TempCheckFrequencySecs` | Whole number from 5 to 300 |

Example result:
```json
{"success":false,"errors":["Stage1.CirculateDurationSecs must be a whole number from 1 to 600"],"config":{"Stage1":{"TempThresholdF":36,"CirculateFrequencyMins":20,"CirculateDurationSecs":10}}}
```

## Parts
Relay: https://www.amazon.com/gp/product/B0BJBDWMM2/ref=ppx_yo_dt_b_search_asin_title?ie=UTF8&psc=1  
Raspberry PI Power: https://www.amazon.com/dp/B01MEF293V?psc=1&ref=ppx_yo2ov_dt_b_product_details  
Solenoid: https://www.amazon.com/dp/B07KCGYQVD?psc=1&ref=ppx_yo2ov_dt_b_product_details  
Raspberry PI 3, 4, or 5
