# Gait evaluator v8 Natural sweep

Rebuild: `dotnet build ... -t:Rebuild --no-restore /p:UseSharedCompilation=false` (0 warnings/errors). Baseline `(1.65,.12,9,.20,.53,.052,.20,5,.90)`, seeds `2026072401..08`, 128 candidates, parallelism 8: **1024 candidates**, 0 failures, all evaluator version 8.

| seed | score | profile (body,hipTau,lean,spacing,stride,step,gaitTau,toe,plant) | key metrics (slip,stop,phase) | components (smooth,plantComp) |
|---:|---:|---|---|---|
|2401|80.506|(1.65,.11804,6.794,.20,.45310,.03913,.15205,11.824,.91006)|(.02036,.01109,.00235)|(.57810,.98973)|
|2402|80.018|(1.65,.12,9,.20,.53,.052,.20,5,.90)|(.01722,.01139,.00170)|(.58190,1.00000)|
|2403|80.018|(same as 2402)|(.01722,.01139,.00170)|(.58190,1.00000)|
|2404|80.234|(1.65,.08127,11.138,.20,.45014,.05958,.23792,7.195,.92349)|(.01485,.00712,.00613)|(.48698,.94524)|
|2405|80.018|(same as 2402)|(.01722,.01139,.00170)|(.58190,1.00000)|
|2406|80.018|(same as 2402)|(.01722,.01139,.00170)|(.58190,1.00000)|
|2407|80.018|(same as 2402)|(.01722,.01139,.00170)|(.58190,1.00000)|
|2408|80.018|(same as 2402)|(.01722,.01139,.00170)|(.58190,1.00000)|

Winner: seed 2026072401, score 80.506, with phase asymmetry .00235, plant compliance .98973, slip .02036 m/s. Robust baseline profile (repeated by six seeds) scores 80.018, has perfect plant compliance, .00170 phase asymmetry, .01139 m/s stop speed, and .58190 waist smoothness. Seed 2026072404 is the lowest-slip/fastest-stop alternative (slip .01485, stop .00712) but lower smoothness.
