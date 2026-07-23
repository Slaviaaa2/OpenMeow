# Gait evaluator v6 — Natural-neighborhood sweep

Build: `dotnet build src/OpenMeow.Lab/OpenMeow.Lab.csproj -t:Rebuild --no-restore` (0 warnings, 0 errors). Baseline: `(1.65,.14,8,.20,.50,.07,.14,10,.90)`. Eight MCP `auto_tune_gait` runs, candidates=128, parallelism=8 (1024 total); all winners evaluator_version=6. Default baseline benchmark reference: ~77.603.

| seed | score | body | hipTau | lean | spacing | stride | step | gaitTau | toe | plant | slip m/s | stop speed | phase asym | smooth | plant comp |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
|2026072301|78.729|1.650|.13807|5.685|.200|.45471|.06028|.10757|9.540|.95270|.02115|.01341|.00943|.56443|.75323|
|2026072302|80.460|1.650|.11732|8.638|.200|.52801|.05187|.19812|4.630|.90136|.01719|.01011|.00072|.52467|.99981|
|2026072303|79.502|1.650|.10907|10.447|.200|.52053|.06341|.19126|3.499|.94684|.01305|.00908|.01023|.50639|.79940|
|2026072304|79.571|1.650|.15156|6.261|.200|.51669|.05715|.19758|4.285|.91737|.01489|.01616|.01689|.58648|.96967|
|2026072305|79.382|1.650|.11970|12.493|.200|.53126|.07728|.11447|14.084|.88315|.03065|.01043|.00146|.52965|.97145|
|2026072306|78.913|1.650|.11165|10.913|.200|.53854|.06205|.17066|8.937|.95942|.01559|.00938|.01209|.51235|.69752|
|2026072307|78.675|1.650|.12902|8.851|.200|.44600|.06904|.15074|11.804|.91668|.01970|.01184|.03352|.54814|.97200|
|2026072308|79.251|1.650|.14091|9.915|.200|.52445|.08059|.11696|3.138|.93185|.02172|.01395|.01180|.56929|.90168|

## Robust candidates

Winner seed 2026072302 (80.460) is the strongest candidate: phase symmetry .99568, plant compliance .99981, stop-settling .34296, and score +2.857 over the ~77.603 baseline reference. Seed 2026072304 is the next high-score/plant-balanced candidate (79.571; plant compliance .96967; waist smoothness .58648). Seed 2026072303 has the lowest slip (.01305 m/s) and fastest settling (.00908 m/s) at 79.502. All runs evaluated 128 candidates with no failures.
