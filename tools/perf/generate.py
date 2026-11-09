#!/usr/bin/env python3
# Usage: python3 tools/perf/generate.py --days 730 --users 5 | docker exec -i plate-perf-db psql -U gym -d gym_perf -v ON_ERROR_STOP=1
# Local synthetic database ONLY; create demo@example.test via create-user first. Replaces synthetic journey data.
import argparse
p=argparse.ArgumentParser(); p.add_argument('--days',type=int,default=730); p.add_argument('--users',type=int,default=5); a=p.parse_args()
assert 1 <= a.days <= 3650 and 1 <= a.users <= 100
print(r'''\set ON_ERROR_STOP on
DO $$ BEGIN IF current_database() <> 'gym_perf' THEN RAISE EXCEPTION 'Only gym_perf is allowed'; END IF; END $$;
BEGIN;
TRUNCATE "Templates", "Sessions", "Bodyweights", "FoodLog", "MaintenanceEstimates", "NutritionTargetDays", "UsualFoods", "ParseRuns" RESTART IDENTITY CASCADE;
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
DO $$ DECLARE u integer; src jsonb; prof jsonb; BEGIN
SELECT to_jsonb(x) INTO STRICT src FROM "AspNetUsers" x WHERE "Email"='demo@example.test';
SELECT to_jsonb(x) INTO STRICT prof FROM "Profiles" x WHERE "UserId"=src->>'Id';
''')
print(f'FOR u IN 2..{a.users} LOOP')
print(r'''INSERT INTO "AspNetUsers" SELECT (jsonb_populate_record(NULL::"AspNetUsers",src || jsonb_build_object('Id','perf-user-'||u,'UserName','perf'||u||'@example.test','NormalizedUserName','PERF'||u||'@EXAMPLE.TEST','Email','perf'||u||'@example.test','NormalizedEmail','PERF'||u||'@EXAMPLE.TEST'))).* ON CONFLICT DO NOTHING;
INSERT INTO "Profiles" SELECT (jsonb_populate_record(NULL::"Profiles",prof || jsonb_build_object('UserId','perf-user-'||u,'DisplayName','Synthetic '||u))).* ON CONFLICT DO NOTHING;
END LOOP; END $$;
UPDATE "Profiles" SET "OnboardedAt"='2026-10-01Z',"HeightCm"=180,"Sex"='Male',"BirthYear"=1996,"Goal"='Maintain',"ActivityLevel"='LowActive',"WeeklySummaryEnabled"=false;
UPDATE "FoodItems" SET "Barcode"='4006381333931' WHERE "Id"=(SELECT min("Id") FROM "FoodItems");
CREATE TEMP TABLE perf_users AS SELECT "Id" uid FROM "AspNetUsers" WHERE "Email"='demo@example.test' OR "Id" LIKE 'perf-user-%';
DO $$ DECLARE u record; tid int; sid int; d int; eid int; seid int; k int; ex int[]; BEGIN
SELECT array_agg("Id" ORDER BY "Id") INTO ex FROM (SELECT "Id" FROM "Exercises" WHERE "OwnerUserId" IS NULL ORDER BY "Id" LIMIT 6) e;
FOR u IN SELECT * FROM perf_users LOOP
INSERT INTO "Templates"("UserId","Name","SortOrder","IsArchived") VALUES(u.uid,'Synthetic six exercises',0,false) RETURNING "Id" INTO tid;
FOR k IN 1..6 LOOP
INSERT INTO "TemplateExercises"("TemplateId","ExerciseId","SortOrder","WorkingSets","RepMin","RepMax","WeightIncrementKg","RestSeconds") VALUES(tid,ex[k],k-1,3,8,12,2.5,120);
END LOOP;
''')
print(f'FOR d IN REVERSE {a.days-1}..0 LOOP')
print(r'''INSERT INTO "Bodyweights"("UserId","Date","WeightKg") VALUES(u.uid,DATE '2026-11-05'-d,80+round((sin(d)*0.5)::numeric,2));
IF d=0 OR d%7 IN(1,3,5) THEN
INSERT INTO "Sessions"("UserId","TemplateId","StartedAt","EndedAt") VALUES(u.uid,tid,(DATE '2026-11-05'-d)+time '15:00',CASE WHEN d=0 THEN NULL ELSE (DATE '2026-11-05'-d)+time '16:00' END) RETURNING "Id" INTO sid;
FOR k IN 1..6 LOOP
INSERT INTO "SessionExercises"("SessionId","ExerciseId","SortOrder","RepMin","RepMax","WorkingSets","WarmupSlots","WeightIncrementKg","RestSeconds") VALUES(sid,ex[k],k-1,8,12,3,0,2.5,120) RETURNING "Id" INTO seid;
IF d>0 THEN
INSERT INTO "Sets"("SessionExerciseId","SetNumber","WeightKg","Reps","IsWarmup","CompletedAt","ClientId") SELECT seid,n,60+(k*2.5),10,false,(DATE '2026-11-05'-d)+time '15:00'+(k*5+n)*interval '1 minute',md5(u.uid||':'||d||':'||k||':'||n)::uuid FROM generate_series(1,3)n;
END IF; END LOOP; END IF;
END LOOP; END LOOP; END $$;
CREATE TEMP TABLE perf_foods AS SELECT "Id",row_number() OVER(ORDER BY "Id") n FROM (SELECT "Id" FROM "FoodItems" WHERE "OwnerUserId" IS NULL ORDER BY "Id" LIMIT 40)x;
''')
print(f'''INSERT INTO "FoodLog"("UserId","Date","Meal","Description","Grams","Kcal","ProteinG","CarbsG","FatG","FiberG","SaturatedFatG","FoodItemId","Source","CreatedAt")
SELECT u.uid,DATE '2026-11-05'-d,(ARRAY['Breakfast','Lunch','Dinner','Snack'])[1+g.nth%4],'Synthetic food '||f.n,150,300,20,40,7,4,1,f."Id",'Search',(DATE '2026-11-05'-d)+time '08:00'+g.nth*interval '1 hour'
FROM perf_users u CROSS JOIN generate_series(0,{a.days-1}) d CROSS JOIN generate_series(1,8)g(nth) JOIN perf_foods f ON f.n=1+(d*8+g.nth)%40;
INSERT INTO "MaintenanceEstimates"("UserId","CalculatedOn","DaysOfData","LoggingCompleteness","EstimatedTdeeKcal","TrendWeightKg","Method","TargetKcal","TargetProteinG","LowConfidence")
SELECT u.uid,DATE '2026-11-05'-d,21,1,2600,80,'Adaptive',2600,144,false FROM perf_users u CROSS JOIN generate_series(0,{a.days-1},7)d;
INSERT INTO "NutritionTargetDays"("UserId","Date","Values")
SELECT u.uid,DATE '2026-11-05'-d,'{{"maintenanceKcal":2600,"kcal":2600,"proteinG":144,"floorApplied":false,"floorKcal":1500,"trendWeightKg":80,"macros":{{"referenceKg":80,"proteinMinG":128,"proteinAimG":144,"fatMinG":58,"fatMaxG":101,"carbsMinG":279,"carbsMaxG":376,"fiberMinG":36,"saturatedFatMaxG":29,"mealProteinG":43,"proteinLowered":false}}}}'::jsonb FROM perf_users u CROSS JOIN generate_series(0,{a.days-1})d;
COMMIT;
ANALYZE;
SELECT json_build_object('days',{a.days},'users',(SELECT count(*) FROM perf_users),'sessions',(SELECT count(*) FROM "Sessions"),'sets',(SELECT count(*) FROM "Sets"),'foodLog',(SELECT count(*) FROM "FoodLog"),'weights',(SELECT count(*) FROM "Bodyweights"),'snapshots',(SELECT count(*) FROM "NutritionTargetDays"),'foods',(SELECT count(*) FROM "FoodItems"));
''')
