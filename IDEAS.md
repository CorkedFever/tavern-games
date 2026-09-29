# Ideas

Games that fit the table and haven't been built yet, with what each would reuse, what it
would need that's new, and a rough size. A game is a rules engine plus a module in Core, an
`IClientGame` in the plugin, and tests; see "Adding a game" in the README. Size is a guess:
**small** is an afternoon, **medium** a day or two, **large** more than that.

Triple Triad and Doman Mahjong are already in the game itself and aren't planned here.

## Dice: everyone bets, one throw

The roulette pattern: chips on a layout, everyone says their bets are down, one throw, the
stacks are paid. All of these reuse `RouletteModule`'s betting flow (place, clear, done, spin,
settle), the layout cells, chips drawn in seat colours, and the spin, float and flight cues.

- **Crown and Anchor.** Three dice with six symbols: crown, anchor, heart, diamond, club,
  spade. Bet on symbols; each die showing yours pays even money. A sailors' game, so it suits
  Limsa venues. New: a symbol face for `DiceRenderer`. *Small.* Chuck-a-luck is the same game
  with numbers instead of symbols and could share the module.
- **Chō-han.** Two dice under a cup; bet odd or even at even money against the house. Reads as
  a Doman or Kugane game. The cup reveal reuses Mia's hidden dice and the tumble. *Small.*
- **Sic Bo.** Three dice; big or small, exact totals from 4 to 17, doubles, triples, a single
  number. Roulette's layout with dice on it, and a long payout table. *Medium.*
- **Craps.** A cut-down table first: pass and don't pass, come and don't come, the field, and
  place bets on 6 and 8; odds later. Unlike the others a round is several throws with a point,
  and the shooter rotates. *Large.*

## Dice: turn by turn

The Pig pattern: one seat on the clock, a tally per seat, push your luck or bank.

- **Farkle.** Six dice. Ones, fives, triples, straights and three pairs score; set scoring dice
  aside and roll the rest on, or bank; a roll with nothing in it loses the turn. First to a
  target (10,000 by default) wins. New: picking which dice to keep by clicking them, and a
  scoring table on the felt. *Medium.*
- **Shut the Box.** Nine tiles, 1 to 9. Roll two dice, flip down tiles that add up to the roll;
  when nothing fits, the open tiles are your score. Each seat plays a box in turn; lowest wins.
  New: the tile row and choosing a combination. *Small.*
- **Knucklebones.** Two seats, a 3 by 3 grid each. Place each roll in a column; matching dice in
  a column multiply; placing a die that matches the other player's column knocks theirs out.
  Tactical and well known. New: the grid, and a two-seat table. *Small to medium.*
- **Yahtzee (Yacht).** Five dice, three rolls, thirteen categories. Everyone knows it. New: the
  scorecard, plus the keep-dice interaction Farkle would also use. *Medium to large.*

## Cards

The card kit is there: `Card`, `Deck`, `PokerHand`, `PotMath`, `CardRenderer`, and the
Blackjack and Hold'em flows for betting rounds and showdowns.

- **Five-card draw.** Ante, five cards each, a betting round, draw up to three, another round,
  showdown. The saloon poker. New: a discard picker, clicking cards to mark them. *Medium.*
- **Baccarat (punto banco).** Bet player, banker or tie; two hands are dealt and drawn by fixed
  rules; banker pays 19 to 20, tie 8 to 1. Nearly nothing to teach, and fast. The roulette
  betting flow with cards on the felt. *Small.*
- **Three-card brag.** Three cards each, betting blind or open, and its own ranking: prial,
  running flush, run, flush, pair, high card. Pub bluffing. New: the ranking and the blind
  rules. *Medium.*
- **Cheat.** Shed your hand by playing cards face down and claiming a rank; anyone can call it.
  Hidden hands through `ToPlayer`. New: picking cards to play and a claim picker; games run
  long. *Medium to large.*
- **Red Dog.** Two cards are dealt; bet that the third falls between them; the narrower the
  spread, the better it pays. A quick side bet of a game. *Small.*

## Events

- **Bingo.** Venues in FFXIV actually run bingo nights. It wants more than six seats, a
  caller's board on the felt in place of plates, and cards that daub themselves. A small change
  to the platform's seat limit as well as a game. *Medium.*
- **A dice horse race.** Six runners advance on dice; everyone bets on the winner before the
  off; the felt shows the track with the runners moving. Pure spectacle for a crowd. *Medium.*

## Suggested order

1. **Crown and Anchor, Chō-han, Baccarat, Red Dog.** An afternoon each, all on flows that exist,
   and each lands in a different corner of the setting.
2. **Farkle and Shut the Box**, which share the pick-the-dice interaction.
3. **Five-card draw and Knucklebones**, the two people will ask for by name.
4. **Sic Bo, Three-card brag, the horse race.**
5. **Cheat, Yahtzee, Craps, Bingo**, the ones with real UI or platform work in them.
