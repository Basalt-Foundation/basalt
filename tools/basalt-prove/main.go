// Command basalt-prove generates a golden Groth16 verification key + proof with gnark, serialized in
// Basalt's Groth16Codec layout (compressed BLS12-381 points), and emits it as JSON. Basalt's
// blst-backed Groth16Verifier must accept it, which is what the C# GnarkCrossEncodingTests assert.
//
// The circuit is deliberately trivial (prove knowledge of X such that X*X == Y, Y public) so the test
// isolates the gnark<->blst point ENCODING, not any circuit logic.
package main

import (
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"

	"github.com/consensys/gnark-crypto/ecc"
	"github.com/consensys/gnark/backend/groth16"
	groth16bls12381 "github.com/consensys/gnark/backend/groth16/bls12-381"
	"github.com/consensys/gnark/frontend"
	"github.com/consensys/gnark/frontend/cs/r1cs"
	"github.com/consensys/gnark/logger"
)

// SquareCircuit proves knowledge of X such that X*X == Y, with Y a public input.
type SquareCircuit struct {
	X frontend.Variable `gnark:",secret"`
	Y frontend.Variable `gnark:",public"`
}

func (c *SquareCircuit) Define(api frontend.API) error {
	api.AssertIsEqual(api.Mul(c.X, c.X), c.Y)
	return nil
}

type golden struct {
	Description   string   `json:"description"`
	Curve         string   `json:"curve"`
	Vk            string   `json:"vk_hex"`
	Proof         string   `json:"proof_hex"`
	PublicInputs  []string `json:"public_inputs_hex"`
	ExpectedValid bool     `json:"expected_valid"`
}

func must(err error, ctx string) {
	if err != nil {
		fmt.Fprintf(os.Stderr, "%s: %v\n", ctx, err)
		os.Exit(1)
	}
}

func main() {
	logger.Disable() // keep stdout pure JSON

	field := ecc.BLS12_381.ScalarField()

	ccs, err := frontend.Compile(field, r1cs.NewBuilder, &SquareCircuit{})
	must(err, "compile")

	pk, vk, err := groth16.Setup(ccs)
	must(err, "setup")

	// Witness: X = 3, Y = 9 (3*3 == 9).
	assignment := &SquareCircuit{X: 3, Y: 9}
	fullWitness, err := frontend.NewWitness(assignment, field)
	must(err, "witness")
	publicWitness, err := fullWitness.Public()
	must(err, "public witness")

	proof, err := groth16.Prove(ccs, pk, fullWitness)
	must(err, "prove")

	// Sanity: gnark's own verifier must accept before we cross-check Basalt.
	must(groth16.Verify(proof, vk, publicWitness), "gnark verify")

	vkc := vk.(*groth16bls12381.VerifyingKey)
	proofc := proof.(*groth16bls12381.Proof)

	// Serialize the VK in Basalt's Groth16Codec order using compressed point bytes:
	//   AlphaG1(48) BetaG2(96) GammaG2(96) DeltaG2(96) IC_count(4 LE) IC[i](48)...
	var vkBytes []byte
	appendG1 := func(dst []byte, b [48]byte) []byte { return append(dst, b[:]...) }
	appendG2 := func(dst []byte, b [96]byte) []byte { return append(dst, b[:]...) }

	alpha := vkc.G1.Alpha.Bytes()
	beta := vkc.G2.Beta.Bytes()
	gamma := vkc.G2.Gamma.Bytes()
	delta := vkc.G2.Delta.Bytes()
	vkBytes = appendG1(vkBytes, alpha)
	vkBytes = appendG2(vkBytes, beta)
	vkBytes = appendG2(vkBytes, gamma)
	vkBytes = appendG2(vkBytes, delta)

	icCount := make([]byte, 4)
	binary.LittleEndian.PutUint32(icCount, uint32(len(vkc.G1.K)))
	vkBytes = append(vkBytes, icCount...)
	for _, k := range vkc.G1.K {
		kb := k.Bytes()
		vkBytes = appendG1(vkBytes, kb)
	}

	// Serialize the proof: A(48) B(96) C(48).
	var proofBytes []byte
	ar := proofc.Ar.Bytes()
	bs := proofc.Bs.Bytes()
	krs := proofc.Krs.Bytes()
	proofBytes = appendG1(proofBytes, ar)
	proofBytes = appendG2(proofBytes, bs)
	proofBytes = appendG1(proofBytes, krs)

	// Public inputs as 32-byte big-endian scalars, in order (here just Y).
	pubVec, ok := publicWitness.Vector().(interface{ Len() int })
	_ = ok
	_ = pubVec
	// Y == 9; encode directly as a 32-byte big-endian scalar for a stable golden value.
	y := make([]byte, 32)
	y[31] = 9

	out := golden{
		Description:   "gnark v0.15 Groth16 BLS12-381, circuit X*X==Y (Y=9), serialized in Basalt Groth16Codec layout",
		Curve:         "BLS12-381",
		Vk:            hex.EncodeToString(vkBytes),
		Proof:         hex.EncodeToString(proofBytes),
		PublicInputs:  []string{hex.EncodeToString(y)},
		ExpectedValid: true,
	}
	enc := json.NewEncoder(os.Stdout)
	enc.SetIndent("", "  ")
	must(enc.Encode(out), "encode json")
}
