# PQSCityPrecisionFix

Fixes the 0.5–1 m misalignment between space-center building modules on
large-radius planets (RSS and similar), caused by float32 world-matrix
composition with planet-radius intermediates in Unity's transform chain.

## The problem

Every object parented under a planet's PQS transform carries planet-radius
magnitudes (±R) through its float32 world-matrix chain. Each transform rounds
independently, so adjacent building modules land up to ~2×ULP(R) apart from
their authored offsets:

| Body radius | float ULP | visible effect |
| --- | --- | --- |
| Kerbin, 600 km | 0.0625 m | invisible |
| Earth (RSS), 6371 km | 0.5 m | modules visibly offset 0.5–1 m |

Measured on KSP 1.12.5 + RSS: 100% of the KSC hierarchy's world coordinates sit
on an exact 0.25 m grid; e.g. the launch pad's fuel-pump housing renders 1.0 m
vertically away from its authored position relative to the pump.

## The fix

After scene setup, the KSC PQSCity is detached from the planet-center parent
and driven every frame in double precision from the same planet transform the
terrain uses. The subtree below it then contains no planet-scale floats, so
building modules compose at full precision. On scene teardown the stock
hierarchy is restored exactly. Bodies below 2^21 m radius (all stock bodies)
are left untouched.

Scope: Space Center and Flight scenes; every PQSCity/PQSCity2 on bodies with
radius >= 2^21 m. Kerbal Konstructs group centers are detected (by reflection,
no dependency) and excluded: KK's editor reads planet-relative positions back
from the transform, which a driven city would corrupt. Precision for KK statics
is planned as a KK-side integration instead.

## License

MIT (matching KSP Community Fixes).

## Building

`build.ps1` compiles `src/PQSCityPrecisionFix.cs` against a KSP 1.12.5 install's
Managed assemblies and deploys to its GameData. Adjust `$csc`/`$ksp` paths.
