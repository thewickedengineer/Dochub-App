"""Rating helpers for personal auto."""
import math
from dataclasses import dataclass

BASE_RATE = 412.0


def territory_factor(zone: str) -> float:
    """Return the multiplier applied to the base rate for a territory zone."""
    factors = {"urban": 1.35, "suburban": 1.1, "rural": 0.9}
    return factors.get(zone, 1.0)


@dataclass
class Rater:
    """Combines base rate, territory and driver factors into a premium."""

    base: float = BASE_RATE

    def premium(self, zone: str, driver_score: float) -> float:
        return round(self.base * territory_factor(zone) * math.sqrt(driver_score), 2)

    def explain(self, zone: str) -> str:
        return f"{zone} applies factor {territory_factor(zone)}"
