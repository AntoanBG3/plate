#!/usr/bin/env python3
"""Builds src/GymTracker.Web/Data/Seed/usda-foods.csv from USDA FoodData Central (public domain, CC0).

Uses the Foundation Foods and SR Legacy CSV downloads and keeps, per food, energy (kcal), protein,
carbohydrate and fat per 100 g. Run once when updating the subset:

    python3 tools/usda/build_usda_subset.py
"""
import csv
import io
import subprocess
import sys
import zipfile
from pathlib import Path

SOURCES = [
    "https://fdc.nal.usda.gov/fdc-datasets/FoodData_Central_foundation_food_csv_2026-04-30.zip",
    "https://fdc.nal.usda.gov/fdc-datasets/FoodData_Central_sr_legacy_food_csv_2018-04.zip",
]
OUT = Path(__file__).resolve().parents[2] / "src/GymTracker.Web/Data/Seed/usda-foods.csv"

# FoodData Central nutrient ids, best first where there are alternatives.
ENERGY = ["1008", "2048", "2047"]   # Energy kcal; Atwater specific; Atwater general
PROTEIN = ["1003"]
FAT = ["1004"]
CARBS = ["1005", "1050"]            # by difference; by summation


def read_zip(url):
    # curl rather than urllib: python.org builds on macOS ship without CA certificates.
    print(f"downloading {url}", file=sys.stderr)
    data = subprocess.run(["curl", "-sfL", "--max-time", "600", url], check=True, capture_output=True).stdout
    return zipfile.ZipFile(io.BytesIO(data))


def member(archive, name):
    path = next(n for n in archive.namelist() if n.endswith("/" + name) or n == name)
    return io.TextIOWrapper(archive.open(path), encoding="utf-8")


def first(values, ids):
    for i in ids:
        if i in values:
            return values[i]
    return None


def main():
    wanted = set(ENERGY + PROTEIN + FAT + CARBS)
    rows = {}
    for url in SOURCES:
        archive = read_zip(url)
        kinds = {"foundation_food", "sr_legacy_food"}
        foods = {r["fdc_id"]: r["description"].strip() for r in csv.DictReader(member(archive, "food.csv")) if r["data_type"] in kinds}
        nutrients = {}
        for r in csv.DictReader(member(archive, "food_nutrient.csv")):
            if r["fdc_id"] in foods and r["nutrient_id"] in wanted and r["amount"]:
                nutrients.setdefault(r["fdc_id"], {})[r["nutrient_id"]] = float(r["amount"])

        for fdc_id, name in foods.items():
            values = nutrients.get(fdc_id, {})
            kcal, protein, fat, carbs = first(values, ENERGY), first(values, PROTEIN), first(values, FAT), first(values, CARBS)
            if None in (kcal, protein, fat) or kcal > 900:
                continue
            key = name.lower()
            if key in rows:
                continue  # Foundation comes first and wins over the older SR Legacy entry
            rows[key] = (fdc_id, name, round(kcal, 1), round(protein, 2), round(carbs or 0.0, 2), round(fat, 2))

    OUT.parent.mkdir(parents=True, exist_ok=True)
    with OUT.open("w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["fdc_id", "name", "kcal", "protein", "carbs", "fat"])
        for row in sorted(rows.values(), key=lambda r: r[1].lower()):
            writer.writerow(row)
    print(f"wrote {len(rows)} foods to {OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
