# Calibration Notes

These notes capture what has been verified from the firmware, live gauge reads, LabVIEW screenshots, and the sample export.

## Verified Data Sources

The gauge host protocol can read sensor data through:

- `READ_SENSOR_SN`
- `READ_SENSOR_CAL`
- `READ_SENSOR_P_POLY`
- `READ_SENSOR_T_POLY`

The firmware forwards these commands to the attached pressure/temperature sensor.

## Sensor Header

Live header example:

```text
S: RefClk .0 Id 1777 Bias 12053700 PStartupMs 5000 PLLClk 169750000
=
```

Parsed fields:

- `RefClk`
- `Id`
- `Bias`
- `PStartupMs`
- `PLLClk`

The `Bias` value is important for reproducing legacy export count columns. The memory record stores 24-bit pressure and temperature count values. Adding the sensor `Bias` puts the count values into the same scale as the LabVIEW export.

## Polynomial Payloads

Pressure and temperature polynomial payloads are ASCII rows of 16-character hexadecimal IEEE-754 double values.

The LabVIEW Engineering Sensor screen shows:

- Pressure polynomial array size: 29
- Temperature polynomial array size: 6

This matches the payloads read through the new CLI.

## Temperature Polynomial Shape

The temperature polynomial has 6 doubles:

```text
[x_min, x_max]
[c0, c1, c2, c3]
```

The LabVIEW screen displays the same arrangement as a flattened array. The exact input transform from raw counts to the `x_min`/`x_max` domain still needs confirmation.

## Pressure Polynomial Shape

The pressure polynomial has 29 doubles. From the row structure observed in live data:

```text
row 0: 2 values
row 1: 2 values
rows 2-6: 5 values each
```

This is likely a bivariate pressure compensation surface using pressure frequency/count and temperature, but the exact equation and normalization still need confirmation before engineering-unit pressure is implemented.

## Next Calibration Task

To implement pressure and temperature conversion safely, we need one of:

- The legacy LabVIEW conversion VI or formula.
- Sensor vendor documentation for the Northstar/XHTI polynomial format.
- A known raw download with matching LabVIEW converted pressure/temperature output and matching sensor polynomial data.

Until then, the app should treat decoded memory data as raw counts plus parsed calibration metadata, not final engineering-unit pressure/temperature.
