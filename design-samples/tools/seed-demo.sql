-- Dev-only demo data for screenshots. Run against the local dev database only:
--   docker exec -i plate-dev-db-1 psql -U gym -d gym -v email=demo@example.test < design-samples/tools/seed-demo.sql
-- Create the user first with the create-user command. Re-running replaces the demo user's data.
\set ON_ERROR_STOP on

select "Id" as uid from "AspNetUsers" where "Email" = :'email' \gset

begin;
select set_config('gym.email', :'email', true);

delete from "Sets" where "SessionExerciseId" in (select se."Id" from "SessionExercises" se join "Sessions" s on s."Id" = se."SessionId" where s."UserId" = :'uid');
delete from "SessionExercises" where "SessionId" in (select "Id" from "Sessions" where "UserId" = :'uid');
delete from "Sessions" where "UserId" = :'uid';
delete from "TemplateExercises" where "TemplateId" in (select "Id" from "Templates" where "UserId" = :'uid');
delete from "Templates" where "UserId" = :'uid';
delete from "Bodyweights" where "UserId" = :'uid';
delete from "FoodLog" where "UserId" = :'uid';
delete from "MaintenanceEstimates" where "UserId" = :'uid';

update "Profiles" set "HeightCm" = 181, "Sex" = 'Male', "BirthYear" = 1996, "Goal" = 'Cut',
    "TargetRatePercentPerWeek" = 0.5, "ProteinGramsPerKg" = 2.0, "DefaultWeightIncrementKg" = 2.5
where "UserId" = :'uid';

create temp table tpl(name text, sort int, ex int[], sets int[], rmin int[], rmax int[], rest int[], base numeric[]);
insert into tpl values
  ('Upper A', 0, array[1,13,17,12,23,27],   array[3,3,3,3,2,2], array[6,6,8,8,10,10],  array[10,10,12,12,15,15], array[180,150,120,120,90,90], array[80,70,47.5,60,32.5,27.5]),
  ('Lower A', 1, array[31,36,33,37,40],     array[3,3,3,3,4],   array[5,8,10,10,10],   array[8,12,15,15,15],     array[210,150,120,90,60],     array[100,90,180,40,70]),
  ('Горна част Б', 2, array[4,15,18,19,25,28], array[3,3,3,3,2,2], array[8,8,8,12,10,10], array[12,12,12,20,15,15], array[150,120,120,60,90,90], array[30,62.5,24,10,16,22.5]);

do $$
declare
  uid text := (select "Id" from "AspNetUsers" where "Email" = current_setting('gym.email'));
  t record; tid int; sid int; seid int; i int; w int; d date; s int; reps int; wt numeric;
  started timestamptz; today date := (now() at time zone 'Europe/Sofia')::date;
  k int := 0;
begin
  for t in select * from tpl order by sort loop
    insert into "Templates"("UserId","Name","SortOrder","IsArchived") values (uid, t.name, t.sort, false) returning "Id" into tid;
    for i in 1..array_length(t.ex,1) loop
      insert into "TemplateExercises"("TemplateId","ExerciseId","SortOrder","WorkingSets","RepMin","RepMax","WeightIncrementKg","RestSeconds")
      values (tid, t.ex[i], i-1, t.sets[i], t.rmin[i], t.rmax[i], null, t.rest[i]);
    end loop;
  end loop;

  -- Six weeks of finished sessions, Mon/Wed/Fri rotating through the templates, slowly progressing.
  for w in 0..5 loop
    foreach s in array array[0,2,4] loop
      d := today - 42 + w*7 + s;
      continue when d >= today;
      select * into t from tpl where sort = k % 3;
      select "Id" into tid from "Templates" where "UserId" = uid and "Name" = t.name;
      started := (d::timestamp + time '18:10') at time zone 'Europe/Sofia';
      insert into "Sessions"("UserId","TemplateId","StartedAt","EndedAt","Notes")
      values (uid, tid, started, started + interval '68 minutes' + (k % 4) * interval '6 minutes', null) returning "Id" into sid;
      for i in 1..array_length(t.ex,1) loop
        insert into "SessionExercises"("SessionId","ExerciseId","SortOrder","RepMin","RepMax","WorkingSets","WeightIncrementKg","RestSeconds")
        values (sid, t.ex[i], i-1, t.rmin[i], t.rmax[i], t.sets[i], 2.5, t.rest[i]) returning "Id" into seid;
        wt := t.base[i] + 2.5 * floor(w / 2.0);
        if i = 1 then
          insert into "Sets"("SessionExerciseId","SetNumber","WeightKg","Reps","Rpe","IsWarmup","CompletedAt","ClientId")
          values (seid, 1, round(wt * 0.5 / 2.5) * 2.5, 8, null, true, started + interval '4 minutes', gen_random_uuid());
        end if;
        for s in 1..t.sets[i] loop
          reps := least(t.rmax[i], t.rmin[i] + (w % 2) * 2 + 2 - (s - 1));
          insert into "Sets"("SessionExerciseId","SetNumber","WeightKg","Reps","Rpe","IsWarmup","CompletedAt","ClientId")
          values (seid, s, wt, reps, null, false, started + (i * 10 + s * 3) * interval '1 minute', gen_random_uuid());
        end loop;
      end loop;
      k := k + 1;
    end loop;
  end loop;

  -- In-progress session started 35 minutes ago on the next template: warmups and two exercises partly done.
  select * into t from tpl where sort = k % 3;
  select "Id" into tid from "Templates" where "UserId" = uid and "Name" = t.name;
  started := now() - interval '35 minutes';
  insert into "Sessions"("UserId","TemplateId","StartedAt","EndedAt","Notes") values (uid, tid, started, null, null) returning "Id" into sid;
  for i in 1..array_length(t.ex,1) loop
    insert into "SessionExercises"("SessionId","ExerciseId","SortOrder","RepMin","RepMax","WorkingSets","WeightIncrementKg","RestSeconds")
    values (sid, t.ex[i], i-1, t.rmin[i], t.rmax[i], t.sets[i], 2.5, t.rest[i]) returning "Id" into seid;
    wt := t.base[i] + 2.5 * 3;
    if i = 1 then
      insert into "Sets" values (default, seid, 1, round(wt * 0.4 / 2.5) * 2.5, 10, null, true, started + interval '3 minutes', gen_random_uuid());
      insert into "Sets" values (default, seid, 2, round(wt * 0.7 / 2.5) * 2.5, 5, null, true, started + interval '6 minutes', gen_random_uuid());
      for s in 1..t.sets[i] loop
        insert into "Sets" values (default, seid, s, wt, t.rmin[i] + 3 - s, null, false, started + (8 + s * 4) * interval '1 minute', gen_random_uuid());
      end loop;
    elsif i = 2 then
      insert into "Sets" values (default, seid, 1, wt, t.rmin[i] + 2, null, false, started + interval '30 minutes', gen_random_uuid());
    end if;
  end loop;

  -- Weigh-ins on most days for seven weeks, drifting down about 0.4 kg a week with daily noise.
  for i in 0..48 loop
    d := today - 48 + i;
    continue when i % 7 = 3 or i % 11 = 5;
    insert into "Bodyweights"("UserId","Date","WeightKg")
    values (uid, d, round((84.6 - i * 0.058 + sin(i * 1.7) * 0.45 + cos(i * 0.9) * 0.25)::numeric, 1));
  end loop;
end $$;

-- Four weeks of food, Bulgarian and English, three or four meals a day, today partly logged.
create temp table menu(meal text, description text, grams numeric, kcal numeric, p numeric, c numeric, f numeric, src text);
insert into menu values
  ('Breakfast','Овесени ядки с кисело мляко и боровинки',320,410,22,58,9,'Ai'),
  ('Breakfast','Баница със сирене',180,560,17,48,33,'Ai'),
  ('Breakfast','Greek yoghurt 2%',250,182,25,10,5,'Search'),
  ('Breakfast','Яйца на очи с филия пълнозърнест хляб',190,330,19,22,18,'Ai'),
  ('Lunch','Пилешко филе на скара',200,330,62,0,7,'Search'),
  ('Lunch','Ориз, варен',180,234,5,51,1,'Search'),
  ('Lunch','Шопска салата със сирене',300,290,11,12,22,'Ai'),
  ('Lunch','Боб чорба',400,340,19,48,8,'Ai'),
  ('Dinner','Кебапчета (3 бр.)',180,450,30,2,36,'Ai'),
  ('Dinner','Пържени картофи',150,465,5,53,25,'Search'),
  ('Dinner','Сьомга на фурна',180,374,40,0,24,'Search'),
  ('Dinner','Мусака',350,560,28,40,32,'Ai'),
  ('Snack','Протеинов шейк с банан',420,330,32,40,4,'Recent'),
  ('Snack','Айрян',330,135,9,12,5,'Barcode'),
  ('Snack','Ябълка',180,94,0,25,0,'Search'),
  ('Snack','Тиквеник',120,310,6,45,12,'Ai');

with days as (select ((now() at time zone 'Europe/Sofia')::date - g) as d, g from generate_series(0, 27) g),
picked as (
  select d, g, m.*, row_number() over (partition by d, m.meal order by md5(d::text || m.description)) as rn
  from days cross join menu m
)
insert into "FoodLog"("UserId","Date","Meal","Description","Grams","Kcal","ProteinG","CarbsG","FatG","FoodItemId","Source","AiModel","CreatedAt")
select :'uid', d, meal, description, grams, kcal, p, c, f, null, src,
       case when src = 'Ai' then 'google/gemini-3.5-flash-lite' end,
       (d::timestamp + case meal when 'Breakfast' then time '08:00' when 'Lunch' then time '13:00' when 'Dinner' then time '19:30' else time '16:00' end) at time zone 'Europe/Sofia'
from picked
where ((meal in ('Breakfast','Dinner') and rn = 1)
   or (meal = 'Lunch' and rn <= 2)
   or (meal = 'Snack' and rn = 1 and g % 3 <> 1))
   and not (g = 0 and meal in ('Dinner','Snack'));

delete from "FoodLog" where "UserId" = :'uid' and "Date" = (now() at time zone 'Europe/Sofia')::date - 9;

commit;
