# RV Circulate Water
This is a service that is intended to help mitigate freezing pipes in RVs by circulating the water through the system when it gets cold.

# ConOps
This service is intended to run on a Raspberry Pi with a relay operated by GPIO.  When activated, the relay triggers a solenoid that allows water to flow back to the tank from some point in the waterline.
The service runs on a specified frequency and checks the outside temperature. At present, it uses Victron’s Cerbo RV management services and a configured temperature sensor.  

You are able to set the temperature threshold and how long to run the pump.  For example, when it is 32* F or below, activate the solenoid for 10 seconds every 15 minutes.  Additionally, it is possible to set multiple “stages” to run the pump more or less frequently depending on the temperature. For example, when it is 10* F or below, run for 20 seconds every 10 minutes.

# Parts
Relay: https://www.amazon.com/gp/product/B0BJBDWMM2/ref=ppx_yo_dt_b_search_asin_title?ie=UTF8&psc=1  
Raspberry PI Power: https://www.amazon.com/dp/B01MEF293V?psc=1&ref=ppx_yo2ov_dt_b_product_details  
Solenoid: https://www.amazon.com/dp/B07KCGYQVD?psc=1&ref=ppx_yo2ov_dt_b_product_details  
Raspberry PI 3 or 4  
